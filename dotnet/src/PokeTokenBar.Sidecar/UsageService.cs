using System.Diagnostics;
using System.Reflection;
using PokeTokenBar.Core.Companions;
using PokeTokenBar.Core.Io;
using PokeTokenBar.Core.Pokedex;
using PokeTokenBar.Core.Sprites;
using PokeTokenBar.Core.Usage;
using PokeTokenBar.Sidecar.Protocol;

namespace PokeTokenBar.Sidecar;

internal sealed class UsageService : IUsageService
{
    private readonly CompanionStore _companions = new();
    private readonly SpriteCache _sprites = new();
    private readonly SpeciesLibrary _library = new();

    /// <summary>How many collected species to fetch artwork for, newest first.</summary>
    private const int CollectionSpriteLimit = 60;

    public async ValueTask<UsageResponse> GetUsageAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();

        var entries = new List<UsageEntry>();
        var filesScanned = 0;
        var filesSkipped = 0;
        long linesTooLong = 0;
        long entriesRejected = 0;
        long duplicatesCollapsed = 0;

        // One scan feeds all three windows, bounded by the earliest of them.
        var since = UsagePeriods.ScanStart(now);

        foreach (var root in TranscriptRoots.Claude())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scan = await ClaudeTranscriptReader
                .ReadDirectoryAsync(root, since, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            entries.AddRange(scan.Entries);
            filesScanned += scan.Stats.FilesScanned;
            filesSkipped += scan.Stats.FilesSkipped;
            linesTooLong += scan.Stats.LinesTooLong;
            entriesRejected += scan.Stats.EntriesRejected;
            duplicatesCollapsed += scan.Stats.DuplicatesCollapsed;
        }

        // Roots can overlap (a relocated config directory, a Desktop session), so the same turn
        // can arrive from two roots and has to be collapsed once more here.
        var deduped = entries
            .GroupBy(static e => e.Id, StringComparer.Ordinal)
            .Select(static group => group.MaxBy(static e => e.Total)!)
            .ToArray();

        var today = LocalDay.For(now);
        var todayTotals = UsageAggregator.ForDay(deduped, today);
        var companion = await EarnAsync(todayTotals, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return new UsageResponse
        {
            Today = todayTotals,
            Companion = companion,
            Week = UsageAggregator.ForRange(deduped, LocalDay.For(UsagePeriods.StartOfWeek(now)), today),
            Month = UsageAggregator.ForRange(deduped, LocalDay.For(UsagePeriods.StartOfMonth(now)), today),
            Scan = new ScanReport
            {
                FilesScanned = filesScanned,
                FilesSkipped = filesSkipped,
                LinesTooLong = linesTooLong,
                EntriesRejected = entriesRejected,
                DuplicatesCollapsed = duplicatesCollapsed,
                ElapsedMilliseconds = (int)stopwatch.ElapsedMilliseconds,
            },
        };
    }

    public ValueTask<SidecarInfoResponse> GetInfoAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var present = TranscriptRoots.Claude().Any(Directory.Exists);

        return ValueTask.FromResult(new SidecarInfoResponse
        {
            Version = version,
            ClaudeTranscriptsPresent = present,
        });
    }

    public async ValueTask<CompanionResponse> ChooseEggAsync(
        int offerIndex,
        CancellationToken cancellationToken)
    {
        using var gate = FileGate.Acquire(_companions.FilePath);

        var loaded = _companions.Load();
        var spend = CompanionKeeper.ChooseEgg(loaded, offerIndex);

        if (!spend.Accepted)
        {
            return await RenderAsync(spend, cancellationToken).ConfigureAwait(false);
        }

        // The egg hatches on purchase, so the species is drawn here rather than incubated. The
        // draw needs the network, which is why the keeper hands back a seed instead of a line.
        var line = await _library
            .DrawAsync(spend.ChosenSeed!.Value, spend.State.Graduated, cancellationToken)
            .ConfigureAwait(false);

        var hatched = spend.State.WithLine(line);
        _companions.Save(hatched);

        return await RenderAsync(
            spend with { State = hatched },
            cancellationToken,
            justHatched: hatched.ToCompanion().CurrentSpeciesId).ConfigureAwait(false);
    }

    public async ValueTask<CompanionResponse> AdvanceCompanionAsync(CancellationToken cancellationToken)
    {
        using var gate = FileGate.Acquire(_companions.FilePath);

        var loaded = await ResolveTruncatedPathAsync(_companions.Load(), cancellationToken)
            .ConfigureAwait(false);

        var spend = CompanionKeeper.Advance(loaded);
        if (spend.Accepted)
        {
            _companions.Save(spend.State);
        }

        return await RenderAsync(spend, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Credits today's usage to the budget and reports the companion untouched. Nothing
    /// progresses here: growth is bought, and a refresh is not a purchase.
    /// </summary>
    private async ValueTask<CompanionResponse> EarnAsync(
        UsageTotals today,
        CancellationToken cancellationToken)
    {
        // One gate around load, credit and save. Every window runs its own sidecar against this
        // same file; without it, two refreshes that interleave both read the same day watermark
        // and both credit the same token delta.
        using var gate = FileGate.Acquire(_companions.FilePath);

        var loaded = await ResolveTruncatedPathAsync(_companions.Load(), cancellationToken)
            .ConfigureAwait(false);
        loaded = await BackfillPokedexAsync(loaded, cancellationToken).ConfigureAwait(false);

        var credited = CompanionKeeper.CreditBudget(loaded, today);
        _companions.Save(credited);

        return await RenderAsync(
            new SpendResult { State = credited, Refusal = SpendRefusal.None },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Most completed lines to reconstruct. A save holding more than this predates the Pokédex
    /// by months of heavy use; the pass is capped so it terminates rather than retrying forever.
    /// </summary>
    private const int MaxBackfilledLineages = 12;

    /// <summary>
    /// Fills in the forms raised on the way to each completed line, for saves written before
    /// the Pokédex existed.
    /// </summary>
    /// <remarks>
    /// A completed line is recorded by its final form alone, so a Pokédex reconstructed from it
    /// shows Venusaur with no Bulbasaur or Ivysaur before it — species that demonstrably were
    /// raised. Runs once: it is flagged done only when every line resolved, so a transient
    /// network failure is retried on the next refresh while a permanent one stops being asked.
    /// Lookups are cached on disk, so a repeat pass after a partial failure costs nothing for
    /// the lines that already succeeded.
    /// </remarks>
    private async ValueTask<CompanionState> BackfillPokedexAsync(
        CompanionState state,
        CancellationToken cancellationToken)
    {
        if (state.PokedexBackfilled || state.Graduated.Count == 0)
        {
            return state;
        }

        var next = state;
        var unresolved = false;

        foreach (var species in state.Graduated.Take(MaxBackfilledLineages))
        {
            var lineage = await _library.LineageOfAsync(species, cancellationToken).ConfigureAwait(false);
            if (lineage.Count == 0)
            {
                unresolved = true;
                continue;
            }

            next = next.WithPokedexEntries(lineage);
        }

        return unresolved ? next : next with { PokedexBackfilled = true };
    }

    /// <summary>
    /// Re-attempts a path that failed to fetch earlier, so a transient network failure does not
    /// leave a multi-form species stuck showing one form forever.
    /// </summary>
    private async ValueTask<CompanionState> ResolveTruncatedPathAsync(
        CompanionState state,
        CancellationToken cancellationToken)
    {
        if (state.PathResolved || !state.HasCompanion)
        {
            return state;
        }

        var resolved = await _library
            .ResolveAsync(state.SpeciesPath[0], state.Rarity, state.Seed, state.Graduated, cancellationToken)
            .ConfigureAwait(false);

        return resolved is null ? state : state.WithResolvedPath(resolved);
    }

    private async ValueTask<CompanionResponse> RenderAsync(
        SpendResult spend,
        CancellationToken cancellationToken,
        int? justHatched = null)
    {
        var state = spend.State;
        var companion = state.ToCompanion();
        var pokedex = state.Pokedex ?? [];

        // A missing sprite is not an error: the companion still has a species, a stage, and
        // progress to show. Animated where the source has it, static otherwise.
        var sprite = state.HasCompanion
            ? await _sprites
                .GetAsync(new SpriteRequest { SpeciesId = companion.CurrentSpeciesId, Animated = true }, cancellationToken)
                .ConfigureAwait(false)
            : new SpriteResult();

        if (state.HasCompanion && sprite.FileName is null)
        {
            sprite = await _sprites
                .GetAsync(new SpriteRequest { SpeciesId = companion.CurrentSpeciesId }, cancellationToken)
                .ConfigureAwait(false);
        }

        // Names for every species the host might render: the current form, the line it is on,
        // and the Pokédex. Gathered once here so the host never has to ask again.
        var mentioned = new HashSet<int>(pokedex);
        mentioned.UnionWith(state.Graduated);
        if (state.HasCompanion)
        {
            mentioned.UnionWith(companion.SpeciesPath);
            mentioned.Add(companion.CurrentSpeciesId);
        }

        // A collected mid-line form was never walked as part of a chain, so its name is
        // unknown. Filled a few at a time so a long Pokédex converges over several refreshes
        // rather than stalling one.
        await _library.EnsureNamesAsync(mentioned, cancellationToken: cancellationToken).ConfigureAwait(false);

        var names = new Dictionary<int, string>();
        foreach (var id in mentioned)
        {
            var name = _library.NameFor(id);
            if (name is not null)
            {
                names[id] = name;
            }
        }

        // Static sprites for the Pokédex: a quarter the size of the animated ones, and a grid
        // of animations would be noise rather than charm. Bounded because the Pokédex grows
        // without limit and each miss is a request. Taken in the order the host renders them,
        // so the cap only ever costs artwork at the end of the list rather than in the middle.
        var collectionSprites = new Dictionary<int, string>();
        foreach (var id in pokedex.Order().Take(CollectionSpriteLimit))
        {
            var art = await _sprites
                .GetAsync(new SpriteRequest { SpeciesId = id }, cancellationToken)
                .ConfigureAwait(false);

            if (art.FileName is not null)
            {
                collectionSprites[id] = art.FileName;
            }
        }

        return new CompanionResponse
        {
            Budget = state.Available,
            Earned = state.Earned,
            Spent = state.Spent,
            HatchPrice = CompanionEconomy.HatchPrice,
            ClickCost = CompanionEconomy.ClickCost,
            OfferCount = state.OfferSeeds?.Count ?? 0,
            HasCompanion = state.HasCompanion,
            CanHatch = !state.HasCompanion && state.Available >= CompanionEconomy.HatchPrice,
            CanAdvance = state.HasCompanion && state.Available >= CompanionEconomy.ClickCost,
            Refusal = spend.Refusal == SpendRefusal.None ? string.Empty : spend.Refusal.ToString(),
            SpeciesId = state.HasCompanion ? companion.CurrentSpeciesId : 0,
            SpeciesName = state.HasCompanion
                ? names.GetValueOrDefault(companion.CurrentSpeciesId, string.Empty)
                : string.Empty,
            StageIndex = state.HasCompanion ? companion.SafeStageIndex : 0,
            TotalForms = state.HasCompanion ? companion.TotalForms : 0,
            StageProgress = state.HasCompanion ? companion.StageProgress : 0,
            TokensAtStage = state.HasCompanion ? companion.TokensAtStage : 0,
            StageThreshold = state.HasCompanion ? companion.StageThreshold : 0,
            Rarity = state.HasCompanion ? companion.Rarity.ToString() : string.Empty,
            ReachedForms = state.HasCompanion ? companion.ReachedForms : [],
            JustEvolved = spend.Evolutions,
            JustGraduated = spend.GraduatedSpeciesId,
            JustHatched = justHatched,
            Pokedex = pokedex,
            Graduated = state.Graduated,
            Names = names,
            CollectionSprites = collectionSprites,
            SpriteFileName = sprite.FileName,
            SpriteDirectory = _sprites.Directory,
        };
    }
}
