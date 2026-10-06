using System.Text.Json.Serialization;

namespace PokeTokenBar.Core.Companions;

/// <summary>
/// Everything persisted about the game between runs.
/// </summary>
/// <remarks>
/// Fields added after the first release are deliberately not <c>required</c>, and are treated as
/// possibly null in <see cref="Sanitized"/>. A property initialiser does not survive
/// source-generated deserialisation, so a save written before a field existed yields null rather
/// than the initialiser's value — and marking one <c>required</c> would make every older save
/// fail to parse and be quarantined instead of migrated.
/// </remarks>
public sealed record CompanionState
{
    /// <summary>
    /// Schema this build writes. Bumped whenever a field is added whose loss would cost the
    /// player something.
    /// </summary>
    /// <remarks>
    /// Version 1 introduced the budget economy; version 2 replaced its single balance with the
    /// earned/spent ledger. The save is shared by every editor window on the machine —
    /// deliberately, so one companion appears everywhere — and separate installs (Code,
    /// Insiders, Cursor) update independently, so two versions can meet over one file. A build
    /// that does not know a field drops it on a round-trip: measured as the pre-economy 0.1.0
    /// sidecar zeroing the balance every five minutes while a newer build was crediting it.
    /// <see cref="CompanionStore"/> uses this to refuse writing over a save from a build it does
    /// not understand.
    /// </remarks>
    public const int SchemaVersion = 2;

    /// <summary>Schema the loaded save was written by; zero for anything pre-economy.</summary>
    public int Version { get; init; }

    /// <summary>
    /// Tokens credited over the life of this save. Only ever increases.
    /// </summary>
    /// <remarks>
    /// A ledger of two monotonic totals rather than one balance, so the numbers can be shown and
    /// checked against each other. It also makes the pre-economy migration computable: under
    /// auto-progress <see cref="TokensAtStage"/> recorded usage that had already been consumed,
    /// so what a player has left over is the difference between what was counted and what was
    /// eaten — a balance field alone carried no way to work that out. Both totals start at
    /// migration; genuine lifetime figures are not recoverable and are not invented.
    /// </remarks>
    public long Earned { get; init; }

    /// <summary>Tokens spent on eggs and growth. Only ever increases.</summary>
    public long Spent { get; init; }

    /// <summary>Tokens available to spend now.</summary>
    [JsonIgnore]
    public long Available => Math.Max(0, Earned - Spent);

    /// <summary>
    /// The single balance written by schema 1, read only so it can be folded into
    /// <see cref="Earned"/> and then left at zero.
    /// </summary>
    [JsonPropertyName("budget")]
    public long LegacyBudget { get; init; }

    /// <summary>
    /// Seeds for the eggs currently offered, one per egg. Empty while a companion is active.
    /// The species behind each is derived from its seed only when chosen, so nothing is
    /// revealed early and a restart cannot redraw a choice not yet made.
    /// </summary>
    public IReadOnlyList<int>? OfferSeeds { get; init; }

    /// <summary>The active companion's line. Empty means no companion, only an offer.</summary>
    public required IReadOnlyList<int> SpeciesPath { get; init; }

    public required int StageIndex { get; init; }

    /// <summary>Tokens spent on the current form.</summary>
    public required long TokensAtStage { get; init; }

    public required Rarity Rarity { get; init; }

    /// <summary>Seed the active line was drawn from.</summary>
    public required int Seed { get; init; }

    /// <summary>
    /// False when the evolution path could not be fetched and may be truncated, so a later
    /// refresh should re-attempt it.
    /// </summary>
    public bool PathResolved { get; init; }

    /// <summary>
    /// Local day the watermark belongs to. Budget is credited from the growth of today's total,
    /// so the watermark has to be discarded when the day rolls over.
    /// </summary>
    public required string WatermarkDay { get; init; }

    /// <summary>Today's token total the last time budget was credited.</summary>
    public required long WatermarkTokens { get; init; }

    /// <summary>
    /// Every species ever owned, in dex order — entered when an egg hatches and again on each
    /// evolution, so it records what has been raised rather than only what was finished.
    /// </summary>
    public IReadOnlyList<int>? Pokedex { get; init; }

    /// <summary>
    /// True once the historical Pokédex backfill has run to completion.
    /// </summary>
    /// <remarks>
    /// A one-time migration, not an ongoing job: every form is recorded as it is reached now, so
    /// only lines completed before the Pokédex existed need reconstructing from their final
    /// form. The flag is what stops a species whose chain cannot be resolved from being looked
    /// up again on every refresh forever.
    /// </remarks>
    public bool PokedexBackfilled { get; init; }

    /// <summary>
    /// True once a single-form path has been sent back for re-resolution.
    /// </summary>
    /// <remarks>
    /// A one-time migration. The chain walker used to offer a branching species alone as a
    /// branch of its own whenever a sibling evolution was outside the sprite range, so a Meowth
    /// could hatch as [52] and be marked resolved. Every such path is a single form, so each
    /// single-form path is resolved once more; a genuine one resolves to itself.
    /// </remarks>
    public bool SingleFormPathsRechecked { get; init; }

    /// <summary>Lines carried all the way to their final form.</summary>
    public required IReadOnlyList<int> Graduated { get; init; }

    /// <summary>True when there is a companion to spend on, rather than an offer to choose from.</summary>
    /// <remarks>
    /// Ignored on the wire: a get-only property is serialised but never deserialised, so
    /// without this the save carries a field that is written, read back as nothing, and only
    /// ever misleads whoever opens the file.
    /// </remarks>
    [JsonIgnore]
    public bool HasCompanion => SpeciesPath.Count > 0;

    public Companion ToCompanion() => new()
    {
        SpeciesPath = SpeciesPath,
        StageIndex = StageIndex,
        TokensAtStage = TokensAtStage,
        Rarity = Rarity,
    };

    /// <summary>A brand new game: no companion, three eggs on offer, nothing banked.</summary>
    public static CompanionState New(int seed) => new()
    {
        Version = SchemaVersion,
        Earned = 0,
        Spent = 0,
        OfferSeeds = CompanionEconomy.NewOffer(seed),
        SpeciesPath = [],
        StageIndex = 0,
        TokensAtStage = 0,
        Rarity = Rarity.Common,
        Seed = seed,
        PathResolved = false,
        SingleFormPathsRechecked = true,
        WatermarkDay = string.Empty,
        WatermarkTokens = 0,
        Pokedex = [],
        Graduated = [],
    };

    /// <summary>Adopts a drawn line as the active companion and records it in the Pokédex.</summary>
    public CompanionState WithLine(EvolutionLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.SpeciesPath.Count == 0)
        {
            return this;
        }

        return (this with
        {
            SpeciesPath = line.SpeciesPath,
            Rarity = line.Rarity,
            StageIndex = 0,
            TokensAtStage = 0,
            PathResolved = line.Resolved,
        }).WithPokedexEntry(line.SpeciesPath[0]);
    }

    /// <summary>Extends a previously truncated path, keeping the stage already reached.</summary>
    public CompanionState WithResolvedPath(EvolutionLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.SpeciesPath.Count == 0
            || SpeciesPath.Count == 0
            || line.SpeciesPath[0] != SpeciesPath[0])
        {
            return this;
        }

        return this with
        {
            SpeciesPath = line.SpeciesPath,
            StageIndex = Math.Clamp(StageIndex, 0, line.SpeciesPath.Count - 1),
            PathResolved = true,
        };
    }

    /// <summary>Records a species as owned. Entries are never repeated, and stay in dex order.</summary>
    public CompanionState WithPokedexEntry(int speciesId) => WithPokedexEntries([speciesId]);

    /// <summary>
    /// Records several species as owned at once — a whole line raised, or one reconstructed.
    /// </summary>
    public CompanionState WithPokedexEntries(IEnumerable<int> speciesIds)
    {
        ArgumentNullException.ThrowIfNull(speciesIds);

        var seen = Pokedex ?? [];
        var added = speciesIds.Where(id => id >= 1 && !seen.Contains(id)).Distinct().ToArray();

        return added.Length == 0
            ? this
            : this with { Pokedex = [.. seen.Concat(added).Order()] };
    }

    /// <summary>
    /// Repairs values that cannot be right. Applied on every load, not only on import: clamping
    /// solely at the boundary leaves an already-bad file to reload badly forever.
    /// </summary>
    public CompanionState Sanitized()
    {
        var path = (SpeciesPath ?? []).Where(static id => id is > 0 and <= 1400).ToArray();
        var offer = (OfferSeeds ?? []).Take(CompanionEconomy.OfferSize).ToArray();
        var owned = Pokedex ?? BackfilledPokedex(path);
        var ledger = MigratedLedger();

        // Neither a companion nor an offer is a dead end rather than a valid state: there would
        // be nothing to spend on and nothing to choose.
        if (path.Length == 0 && offer.Length == 0)
        {
            return New(Seed) with
            {
                Version = Math.Max(Version, SchemaVersion),
                Earned = ledger.Earned,
                Spent = ledger.Spent,
                Pokedex = [.. Keep(owned).Order()],
                Graduated = Keep(Graduated),
                WatermarkDay = WatermarkDay.Length <= 10 ? WatermarkDay : string.Empty,
                WatermarkTokens = Math.Max(0, WatermarkTokens),
            };
        }

        return this with
        {
            // Stamped on the way out, not on the way in: a save that reaches here has been
            // migrated to what this build understands, whatever it was written by.
            Version = Math.Max(Version, SchemaVersion),
            Earned = ledger.Earned,
            Spent = ledger.Spent,
            LegacyBudget = 0,
            OfferSeeds = offer,
            SpeciesPath = path,
            PathResolved = PathResolved && (SingleFormPathsRechecked || path.Length != 1),
            SingleFormPathsRechecked = true,
            StageIndex = path.Length == 0 ? 0 : Math.Clamp(StageIndex, 0, path.Length - 1),
            TokensAtStage = Math.Clamp(TokensAtStage, 0, PokemonBalance.GraduationTotal(Rarity)),
            WatermarkTokens = Math.Max(0, WatermarkTokens),
            WatermarkDay = WatermarkDay.Length <= 10 ? WatermarkDay : string.Empty,
            // Sorted, because a Pokédex is read by number. Graduated is left in completion
            // order, which is real information rather than an artefact of insertion.
            Pokedex = [.. Keep(owned).Order()],
            Graduated = Keep(Graduated),
        };
    }

    /// <summary>
    /// The ledger for this save, migrating whatever the older schemas recorded.
    /// </summary>
    /// <remarks>
    /// Schema 0 ran auto-progress, where <see cref="TokensAtStage"/> was usage already eaten by
    /// the companion and <see cref="WatermarkTokens"/> was the usage counted toward it. What a
    /// player has left is therefore the difference — measured on a real save as 304M counted
    /// against 291M eaten, leaving 13M rather than the nothing a fresh ledger would have given
    /// them. Clamped at zero because a companion grown over several days can have eaten more
    /// than today's total.
    ///
    /// Schema 1 stored a plain balance, so that balance becomes the opening credit.
    /// </remarks>
    private (long Earned, long Spent) MigratedLedger()
    {
        if (Version >= 2)
        {
            var earned = Math.Max(0, Earned);
            return (earned, Math.Clamp(Spent, 0, earned));
        }

        var opening = Version == 1
            ? Math.Max(0, LegacyBudget)
            : Math.Max(0, Math.Max(0, WatermarkTokens) - Math.Max(0, TokensAtStage));

        return (opening, 0);
    }

    /// <summary>
    /// What a save written before the Pokédex existed can prove was owned: every completed line,
    /// plus the forms the active companion has actually reached.
    /// </summary>
    /// <remarks>
    /// Only reached for a null Pokédex, never an empty one. A new game has an empty list and must
    /// keep it; a pre-Pokédex save has no list at all, and showing an established player an empty
    /// Pokédex would read as lost progress rather than as a new feature.
    /// </remarks>
    private int[] BackfilledPokedex(int[] path)
    {
        var reached = path.Take(Math.Clamp(StageIndex, 0, Math.Max(0, path.Length - 1)) + 1);
        return [.. (Graduated ?? []).Concat(reached).Distinct()];
    }

    private static int[] Keep(IReadOnlyList<int>? ids) =>
        (ids ?? []).Where(static id => id is > 0 and <= 1400).Take(2000).ToArray();
}
