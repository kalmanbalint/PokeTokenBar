using PokeTokenBar.Core.Companions;

namespace PokeTokenBar.Core.Tests;

/// <summary>Shared fixtures for the earn-and-spend tests.</summary>
internal static class GameStates
{
    /// <summary>A new game with enough banked to act.</summary>
    public static CompanionState Funded(long budget = 1_000_000_000) =>
        CompanionState.New(1) with { Earned = budget };

    /// <summary>A funded game with a three-form common line already active.</summary>
    public static CompanionState WithCompanion(long budget = 1_000_000_000) =>
        Funded(budget).WithLine(new EvolutionLine
        {
            SpeciesPath = [1, 2, 3],
            Rarity = Rarity.Common,
            Resolved = true,
        }) with { OfferSeeds = [] };
}

public sealed class EvolutionLinesTests
{
    [Fact]
    public void EverySpeciesIdIsInSpriteRange()
    {
        foreach (var line in EvolutionLines.All)
        {
            Assert.NotEmpty(line.SpeciesPath);
            Assert.All(line.SpeciesPath, id => Assert.InRange(id, 1, 1400));
        }
    }

    [Fact]
    public void TheSameSeedAlwaysDrawsTheSameLine()
    {
        // A restart must not redraw a different species.
        for (var seed = 0; seed < 500; seed += 37)
        {
            Assert.Equal(EvolutionLines.FromSeed(seed, []), EvolutionLines.FromSeed(seed, []));
        }
    }

    [Fact]
    public void DrawsSomethingForAnySeedIncludingNegatives()
    {
        foreach (var seed in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        {
            Assert.NotNull(EvolutionLines.FromSeed(seed, []));
            Assert.NotEmpty(EvolutionLines.FromSeed(seed, []).SpeciesPath);
        }
    }

    [Fact]
    public void CommonLinesAreDrawnMoreOftenThanLegendary()
    {
        var counts = new Dictionary<Rarity, int>();
        for (var seed = 0; seed < 20_000; seed++)
        {
            var rarity = EvolutionLines.FromSeed(seed, []).Rarity;
            counts[rarity] = counts.GetValueOrDefault(rarity) + 1;
        }

        Assert.True(counts[Rarity.Common] > counts.GetValueOrDefault(Rarity.Uncommon));
        Assert.True(counts.GetValueOrDefault(Rarity.Uncommon) > counts.GetValueOrDefault(Rarity.Legendary));
    }
}

public sealed class BudgetCreditTests
{
    [Fact]
    public void FirstReadingOfADayCreditsInFull()
    {
        var credited = CompanionKeeper.CreditBudget(CompanionState.New(1), "2026-09-09", 1_000_000);

        Assert.Equal(1_000_000, credited.Available);
        Assert.Equal("2026-09-09", credited.WatermarkDay);
        Assert.Equal(1_000_000, credited.WatermarkTokens);
    }

    [Fact]
    public void OnlyTheGrowthSinceTheLastReadingIsCredited()
    {
        // Readings are absolute totals; treating each as new usage would multiply the budget by
        // the number of refreshes.
        var first = CompanionKeeper.CreditBudget(CompanionState.New(1), "2026-09-09", 1_000_000);

        var second = CompanionKeeper.CreditBudget(first, "2026-09-09", 1_500_000);

        Assert.Equal(1_500_000, second.Available);
    }

    [Fact]
    public void ARefreshWithNoNewUsageCreditsNothing()
    {
        var first = CompanionKeeper.CreditBudget(CompanionState.New(1), "2026-09-09", 1_000_000);

        var second = CompanionKeeper.CreditBudget(first, "2026-09-09", 1_000_000);

        Assert.Equal(first.Available, second.Available);
    }

    [Fact]
    public void ADayRolloverRestartsTheWatermarkWithoutLosingBudget()
    {
        // Today's total resets at midnight, so a stale watermark would compute a negative delta.
        var yesterday = CompanionKeeper.CreditBudget(CompanionState.New(1), "2026-09-09", 5_000_000);

        var today = CompanionKeeper.CreditBudget(yesterday, "2026-09-10", 2_000_000);

        Assert.Equal(7_000_000, today.Available);
        Assert.Equal("2026-09-10", today.WatermarkDay);
    }

    [Fact]
    public void BudgetNeverMovesBackwards()
    {
        // A lower reading than the watermark — a deleted transcript, a clock change — must not
        // subtract from the budget.
        var first = CompanionKeeper.CreditBudget(CompanionState.New(1), "2026-09-09", 5_000_000);

        var second = CompanionKeeper.CreditBudget(first, "2026-09-09", 1_000);

        Assert.Equal(first.Available, second.Available);
    }

    [Fact]
    public void CreditingNeverAdvancesTheCompanion()
    {
        // The whole point of the economy: usage banks tokens, it does not spend them. A credit
        // far larger than a graduation total must still leave the companion exactly as it was.
        var state = GameStates.WithCompanion(budget: 0);

        var credited = CompanionKeeper.CreditBudget(state, "2026-09-10", 10_000_000_000);

        Assert.Equal(0, credited.TokensAtStage);
        Assert.Equal(0, credited.StageIndex);
        Assert.Equal(state.SpeciesPath, credited.SpeciesPath);
        Assert.Empty(credited.Graduated);
        Assert.Equal(10_000_000_000, credited.Available);
    }

    [Fact]
    public void CreditingNeverTakesAnEggFromTheOffer()
    {
        var credited = CompanionKeeper.CreditBudget(CompanionState.New(1), "2026-09-10", 10_000_000_000);

        Assert.False(credited.HasCompanion);
        Assert.Equal(CompanionEconomy.OfferSize, credited.OfferSeeds!.Count);
    }
}

public sealed class EggChoiceTests
{
    [Fact]
    public void ANewGameOffersEggsAndHasNoCompanion()
    {
        var state = CompanionState.New(42);

        Assert.False(state.HasCompanion);
        Assert.Equal(CompanionEconomy.OfferSize, state.OfferSeeds!.Count);
        Assert.Equal(0, state.Available);
        Assert.Empty(state.Pokedex!);
    }

    [Fact]
    public void TheSameSaveAlwaysOffersTheSameEggs()
    {
        // A restart must not reshuffle a choice not yet made.
        Assert.Equal(CompanionState.New(9).OfferSeeds, CompanionState.New(9).OfferSeeds);
    }

    [Fact]
    public void TheOfferedEggsAreDistinctFromOneAnother()
    {
        var seeds = CompanionState.New(7).OfferSeeds!;

        Assert.Equal(seeds.Count, seeds.Distinct().Count());
    }

    [Fact]
    public void ChoosingSpendsExactlyTheHatchPrice()
    {
        var spend = CompanionKeeper.ChooseEgg(GameStates.Funded(7_000_000), 1);

        Assert.True(spend.Accepted);
        Assert.Equal(2_000_000, spend.State.Available);
    }

    [Fact]
    public void ChoosingAdoptsTheChosenEggsSeed()
    {
        var state = GameStates.Funded();
        var expected = state.OfferSeeds![2];

        var spend = CompanionKeeper.ChooseEgg(state, 2);

        Assert.Equal(expected, spend.ChosenSeed);
        Assert.Equal(expected, spend.State.Seed);
    }

    [Fact]
    public void TheEggsNotChosenAreDiscarded()
    {
        var spend = CompanionKeeper.ChooseEgg(GameStates.Funded(), 0);

        Assert.Empty(spend.State.OfferSeeds!);
    }

    [Fact]
    public void TooLittleBudgetIsRefusedAndKeepsTheOfferIntact()
    {
        var state = GameStates.Funded(CompanionEconomy.HatchPrice - 1);

        var spend = CompanionKeeper.ChooseEgg(state, 0);

        Assert.Equal(SpendRefusal.NotEnoughBudget, spend.Refusal);
        Assert.Equal(state.Available, spend.State.Available);
        Assert.Equal(state.OfferSeeds, spend.State.OfferSeeds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void AnIndexOutsideTheOfferIsRefusedRatherThanClamped(int index)
    {
        // The index arrives from a click in a webview. Redirecting a bad one onto a valid egg
        // would spend the budget on a species nobody chose.
        var spend = CompanionKeeper.ChooseEgg(GameStates.Funded(), index);

        Assert.Equal(SpendRefusal.NoSuchEgg, spend.Refusal);
        Assert.Equal(1_000_000_000, spend.State.Available);
        Assert.False(spend.State.HasCompanion);
    }

    [Fact]
    public void ChoosingIsRefusedWhileACompanionIsActive()
    {
        var spend = CompanionKeeper.ChooseEgg(GameStates.WithCompanion(), 0);

        Assert.Equal(SpendRefusal.AlreadyHasCompanion, spend.Refusal);
    }

    [Fact]
    public void HatchingEntersTheFirstFormInThePokedex()
    {
        var hatched = GameStates.WithCompanion();

        Assert.Contains(1, hatched.Pokedex!);
    }
}

public sealed class AdvanceTests
{
    [Fact]
    public void AdvancingWithoutACompanionIsRefused()
    {
        var spend = CompanionKeeper.Advance(GameStates.Funded());

        Assert.Equal(SpendRefusal.NoCompanion, spend.Refusal);
    }

    [Fact]
    public void AdvancingWithoutEnoughBudgetSpendsNothing()
    {
        var state = GameStates.WithCompanion(CompanionEconomy.ClickCost - 1);

        var spend = CompanionKeeper.Advance(state);

        Assert.Equal(SpendRefusal.NotEnoughBudget, spend.Refusal);
        Assert.Equal(state.Available, spend.State.Available);
        Assert.Equal(0, spend.State.TokensAtStage);
    }

    [Fact]
    public void OnePressMovesTheClickCostFromBudgetIntoProgress()
    {
        var spend = CompanionKeeper.Advance(GameStates.WithCompanion(100_000_000));

        Assert.True(spend.Accepted);
        Assert.Equal(100_000_000 - CompanionEconomy.ClickCost, spend.State.Available);
        Assert.Equal(CompanionEconomy.ClickCost, spend.State.TokensAtStage);
        Assert.Empty(spend.Evolutions);
    }

    [Fact]
    public void CrossingAThresholdEvolvesAndRecordsThePokedexEntry()
    {
        var state = GameStates.WithCompanion() with
        {
            TokensAtStage = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0) - 1,
        };

        var spend = CompanionKeeper.Advance(state);

        Assert.Equal([2], spend.Evolutions);
        Assert.Equal(1, spend.State.StageIndex);
        Assert.Contains(2, spend.State.Pokedex!);
    }

    [Fact]
    public void ThePokedexIsHeldInDexOrderNotArrivalOrder()
    {
        // A Pokédex is read by number. Eevee raised before Bulbasaur must still list second.
        var state = GameStates.WithCompanion() with { Pokedex = [133, 1] };

        var spend = CompanionKeeper.Advance(state with
        {
            TokensAtStage = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0) - 1,
        });

        Assert.Equal([1, 2, 133], spend.State.Pokedex);
    }

    [Fact]
    public void AWholeLineCanBeRecordedAtOnce()
    {
        var state = GameStates.WithCompanion() with { Pokedex = [133] };

        Assert.Equal([1, 2, 3, 133], state.WithPokedexEntries([3, 1, 2]).Pokedex);
    }

    [Fact]
    public void PokedexEntriesAreNeverRepeated()
    {
        var state = GameStates.WithCompanion() with
        {
            TokensAtStage = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0) - 1,
            Pokedex = [1, 2],
        };

        var spend = CompanionKeeper.Advance(state);

        Assert.Equal([1, 2], spend.State.Pokedex);
    }

    [Fact]
    public void GraduationRetiresTheLineAndOffersFreshEggs()
    {
        var state = GameStates.WithCompanion() with
        {
            StageIndex = 2,
            TokensAtStage = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 2) - 1,
        };

        var spend = CompanionKeeper.Advance(state);

        Assert.Equal(3, spend.GraduatedSpeciesId);
        Assert.Contains(3, spend.State.Graduated);
        Assert.False(spend.State.HasCompanion);
        Assert.Equal(CompanionEconomy.OfferSize, spend.State.OfferSeeds!.Count);
    }

    [Fact]
    public void GraduationKeepsWhatWasEarnedAndCollected()
    {
        var state = GameStates.WithCompanion(500_000_000) with
        {
            StageIndex = 2,
            TokensAtStage = PokemonBalance.PhaseThreshold(Rarity.Common, 3, 2) - 1,
            Pokedex = [1, 2, 3],
        };

        var spend = CompanionKeeper.Advance(state);

        Assert.Equal(500_000_000 - CompanionEconomy.ClickCost, spend.State.Available);
        Assert.Equal([1, 2, 3], spend.State.Pokedex);
    }

    [Fact]
    public void AWholeLineCostsItsGraduationTotalInPresses()
    {
        // The press count is the only thing ClickCost changes; the tokens a line costs stay
        // calibrated to elapsed time.
        var state = GameStates.WithCompanion(long.MaxValue / 2);
        var presses = 0;

        while (state.HasCompanion && presses < 10_000)
        {
            state = CompanionKeeper.Advance(state).State;
            presses++;
        }

        Assert.Equal(PokemonBalance.GraduationTotal(Rarity.Common) / CompanionEconomy.ClickCost, presses);
    }
}

public sealed class CompanionStateSanitizerTests
{
    private static CompanionState Active() => GameStates.WithCompanion();

    [Fact]
    public void RepairsAnOutOfRangeStage()
    {
        var repaired = (Active() with { StageIndex = 99 }).Sanitized();

        Assert.InRange(repaired.StageIndex, 0, repaired.SpeciesPath.Count - 1);
    }

    [Fact]
    public void DropsImplausibleSpeciesIds()
    {
        var repaired = (Active() with { SpeciesPath = [1, -5, 99_999, 3] }).Sanitized();

        Assert.Equal([1, 3], repaired.SpeciesPath);
    }

    [Fact]
    public void StartsOverWhenThereIsNeitherACompanionNorAnOffer()
    {
        var broken = Active() with { SpeciesPath = [-1, 0, 99_999], OfferSeeds = [] };

        var repaired = broken.Sanitized();

        Assert.False(repaired.HasCompanion);
        Assert.Equal(CompanionEconomy.OfferSize, repaired.OfferSeeds!.Count);
    }

    [Fact]
    public void StartingOverKeepsTheBudgetAndPokedex()
    {
        // A repair is not a punishment: what was earned and collected survives it.
        var broken = Active() with
        {
            SpeciesPath = [],
            OfferSeeds = [],
            Earned = 40_000_000,
            Pokedex = [4, 5],
            Graduated = [6],
        };

        var repaired = broken.Sanitized();

        Assert.Equal(40_000_000, repaired.Available);
        Assert.Equal([4, 5], repaired.Pokedex);
        Assert.Equal([6], repaired.Graduated);
    }

    [Fact]
    public void ClampsTokensThatExceedAWholeLine()
    {
        var repaired = (Active() with { TokensAtStage = long.MaxValue }).Sanitized();

        Assert.InRange(repaired.TokensAtStage, 0, PokemonBalance.GraduationTotal(repaired.Rarity));
    }

    [Fact]
    public void ClampsNegativeAmounts()
    {
        var repaired = (Active() with
        {
            TokensAtStage = -500,
            WatermarkTokens = -1,
            Earned = -9,
        }).Sanitized();

        Assert.Equal(0, repaired.TokensAtStage);
        Assert.Equal(0, repaired.WatermarkTokens);
        Assert.Equal(0, repaired.Available);
    }

    [Fact]
    public void DropsExtraEggsBeyondTheOfferSize()
    {
        var repaired = (CompanionState.New(1) with { OfferSeeds = [1, 2, 3, 4, 5] }).Sanitized();

        Assert.Equal(CompanionEconomy.OfferSize, repaired.OfferSeeds!.Count);
    }

    [Fact]
    public void DiscardsAnImplausibleWatermarkDay()
    {
        var state = Active() with { WatermarkDay = new string('x', 5_000) };

        Assert.Equal(string.Empty, state.Sanitized().WatermarkDay);
    }
}

public sealed class CompanionStoreTests
{
    [Fact]
    public void RoundTripsThroughDisk()
    {
        using var directory = new TempStoreDirectory();
        var original = CompanionKeeper
            .Advance(GameStates.WithCompanion(100_000_000)).State;

        new CompanionStore(directory.File).Save(original);
        var loaded = new CompanionStore(directory.File).Load();

        Assert.Equal(original.SpeciesPath, loaded.SpeciesPath);
        Assert.Equal(original.TokensAtStage, loaded.TokensAtStage);
        Assert.Equal(original.Available, loaded.Available);
        Assert.Equal(original.Spent, loaded.Spent);
        Assert.Equal(original.Pokedex, loaded.Pokedex);
        Assert.Equal(original.Rarity, loaded.Rarity);
        Assert.Equal(original.WatermarkDay, loaded.WatermarkDay);
    }

    [Fact]
    public void RoundTripsAnUnchosenOffer()
    {
        // The offer is the only record of which three eggs were presented; losing it on a
        // restart would silently redraw a choice already shown.
        using var directory = new TempStoreDirectory();
        var original = CompanionState.New(21);

        new CompanionStore(directory.File).Save(original);

        Assert.Equal(original.OfferSeeds, new CompanionStore(directory.File).Load().OfferSeeds);
    }

    [Fact]
    public void PersistsRarityByNameSoReorderingCannotReinterpretIt()
    {
        // Rarity decides graduation cost; a numeric enum would silently change meaning if a
        // tier were ever inserted.
        using var directory = new TempStoreDirectory();
        new CompanionStore(directory.File).Save(GameStates.WithCompanion() with { Rarity = Rarity.Legendary });

        Assert.Contains("Legendary", File.ReadAllText(directory.File), StringComparison.Ordinal);
    }

    [Fact]
    public void SetsACorruptFileAsideAndStartsFresh()
    {
        using var directory = new TempStoreDirectory();
        File.WriteAllText(directory.File, "{ this is not json");
        var store = new CompanionStore(directory.File);

        var loaded = store.Load();

        Assert.True(store.RecoveredFromCorruption);
        Assert.NotEmpty(loaded.OfferSeeds!);
        Assert.True(File.Exists(directory.File + ".corrupt"), "the bad file must be kept for inspection");
    }

    [Fact]
    public void StartsANewGameWhenNoFileExistsYet()
    {
        using var directory = new TempStoreDirectory();
        var store = new CompanionStore(directory.File);

        var loaded = store.Load();

        Assert.False(store.RecoveredFromCorruption);
        Assert.True(store.StartedFresh);
        Assert.Equal(CompanionEconomy.OfferSize, loaded.OfferSeeds!.Count);
    }

    [Fact]
    public void SanitizesOnLoadNotOnlyOnSave()
    {
        // A file already holding a bad value must not reload badly forever.
        using var directory = new TempStoreDirectory();
        File.WriteAllText(
            directory.File,
            """{"speciesPath":[1,2,3],"stageIndex":99,"tokensAtStage":-4,"budget":-7,"rarity":"Common","seed":1,"watermarkDay":"","watermarkTokens":-9,"graduated":[]}""");

        var loaded = new CompanionStore(directory.File).Load();

        Assert.InRange(loaded.StageIndex, 0, 2);
        Assert.Equal(0, loaded.TokensAtStage);
        Assert.Equal(0, loaded.WatermarkTokens);
        Assert.Equal(0, loaded.Available);
    }

    [Fact]
    public void StampsTheSchemaVersionItWrote()
    {
        using var directory = new TempStoreDirectory();
        new CompanionStore(directory.File).Save(CompanionState.New(1));

        Assert.Contains(
            $"\"version\": {CompanionState.SchemaVersion}",
            File.ReadAllText(directory.File),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotPersistComputedProperties()
    {
        // A get-only property is serialised but never read back, so it only misleads whoever
        // opens the file — and invites a future reader to trust it.
        using var directory = new TempStoreDirectory();
        new CompanionStore(directory.File).Save(GameStates.WithCompanion());

        Assert.DoesNotContain("hasCompanion", File.ReadAllText(directory.File), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToOverwriteASaveFromANewerBuild()
    {
        // Every window on the machine shares this file and installs update independently, so
        // an older build can meet a newer save. Rewriting it would drop whatever fields this
        // build has never heard of.
        using var directory = new TempStoreDirectory();
        var newer = $$"""{"version":{{CompanionState.SchemaVersion + 1}},"speciesPath":[25],"stageIndex":0,"tokensAtStage":0,"budget":777,"rarity":"Common","seed":1,"watermarkDay":"","watermarkTokens":0,"graduated":[],"somethingNewWeCannotSee":42}""";
        File.WriteAllText(directory.File, newer);
        var store = new CompanionStore(directory.File);

        store.Save(CompanionState.New(2));

        Assert.True(store.RefusedToDowngrade);
        Assert.Equal(newer, File.ReadAllText(directory.File));
    }

    [Fact]
    public void SavesNormallyOverASaveFromAnOlderBuild()
    {
        using var directory = new TempStoreDirectory();
        File.WriteAllText(
            directory.File,
            """{"speciesPath":[133,134],"stageIndex":0,"tokensAtStage":0,"rarity":"Uncommon","seed":77,"watermarkDay":"","watermarkTokens":0,"graduated":[]}""");
        var store = new CompanionStore(directory.File);

        store.Save(CompanionState.New(2) with { Earned = 999 });

        Assert.False(store.RefusedToDowngrade);
        Assert.Equal(999, new CompanionStore(directory.File).Load().Available);
    }

    [Fact]
    public void LeavesNoTemporaryFileBehind()
    {
        using var directory = new TempStoreDirectory();
        var store = new CompanionStore(directory.File);

        store.Save(CompanionState.New(1));

        Assert.False(File.Exists(directory.File + ".tmp"));
    }

    private sealed class TempStoreDirectory : IDisposable
    {
        public TempStoreDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "ptb-store-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Root);
            File = Path.Combine(Root, "companion.json");
        }

        public string Root { get; }

        public string File { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// Saves written before the economy existed. These are real files from earlier builds: the
/// fields the economy added are absent, and a source-generated deserializer yields null for
/// them rather than a property initialiser's value.
/// </summary>
public sealed class SaveMigrationTests
{
    [Fact]
    public void ASaveFromBeforeTheEconomyKeepsItsCompanion()
    {
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"speciesPath":[133,134],"stageIndex":1,"tokensAtStage":1200000,"rarity":"Uncommon","seed":77,"watermarkDay":"2026-09-10","watermarkTokens":999999999,"graduated":[3],"pathResolved":true}""");

        var loaded = new CompanionStore(directory.File).Load();

        Assert.True(loaded.HasCompanion);
        Assert.Equal([133, 134], loaded.SpeciesPath);
        Assert.Equal(1, loaded.StageIndex);
        Assert.Equal([3], loaded.Graduated);
        // Counted 999,999,999 and 1,200,000 of it already eaten by auto-progress.
        Assert.Equal(998_799_999, loaded.Available);
    }

    [Fact]
    public void TheLeftoverFromAutoProgressIsCreditedRatherThanLost()
    {
        // Measured against a real save: under auto-progress tokensAtStage was usage the
        // companion had already consumed and watermarkTokens was the usage counted toward it,
        // so what the player still has is the difference. Starting the ledger at zero would
        // quietly confiscate a day's earnings.
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"speciesPath":[133,134],"stageIndex":0,"tokensAtStage":308000000,"rarity":"Uncommon","seed":77,"watermarkDay":"2026-09-10","watermarkTokens":350000000,"graduated":[],"pathResolved":true}""");

        var loaded = new CompanionStore(directory.File).Load();

        Assert.Equal(42_000_000, loaded.Available);
        Assert.Equal(42_000_000, loaded.Earned);
        Assert.Equal(0, loaded.Spent);
    }

    [Fact]
    public void ACompanionGrownOverDaysDoesNotProduceANegativeLedger()
    {
        // Several days of growth can exceed today's total, and a negative balance would either
        // block every spend or wrap into a fortune.
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"speciesPath":[1,2,3],"stageIndex":2,"tokensAtStage":700000000,"rarity":"Common","seed":1,"watermarkDay":"2026-09-10","watermarkTokens":5000000,"graduated":[],"pathResolved":true}""");

        var loaded = new CompanionStore(directory.File).Load();

        Assert.Equal(0, loaded.Available);
        Assert.Equal(0, loaded.Earned);
    }

    [Fact]
    public void ABalanceFromTheFirstEconomyBuildBecomesTheOpeningCredit()
    {
        // Schema 1 stored a single balance and no ledger; it carries over as earned-but-unspent
        // rather than being re-derived, because by then tokensAtStage no longer meant consumed
        // usage.
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"version":1,"budget":4034791,"speciesPath":[133,134],"stageIndex":0,"tokensAtStage":291118248,"rarity":"Uncommon","seed":77,"watermarkDay":"2026-09-10","watermarkTokens":304183929,"graduated":[3],"pathResolved":true,"pokedex":[3,133]}""");

        var loaded = new CompanionStore(directory.File).Load();

        Assert.Equal(4_034_791, loaded.Available);
        Assert.Equal(0, loaded.Spent);
    }

    [Fact]
    public void TheLegacyBalanceIsNotLeftInTheFileToBeCountedTwice()
    {
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"version":1,"budget":5000,"speciesPath":[1,2,3],"stageIndex":0,"tokensAtStage":0,"rarity":"Common","seed":1,"watermarkDay":"","watermarkTokens":0,"graduated":[]}""");
        var store = new CompanionStore(directory.File);

        store.Save(store.Load());

        Assert.Contains("\"budget\": 0", File.ReadAllText(directory.File), StringComparison.Ordinal);
        Assert.Equal(5_000, new CompanionStore(directory.File).Load().Available);
    }

    [Fact]
    public void AnEggFromTheIncubationBuildBecomesTheCompanionItWasGrowing()
    {
        // The egg has already been paid for under the old rules, so it hatches rather than
        // being taken away and re-offered.
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"speciesPath":[133,134],"stageIndex":0,"tokensAtStage":1200000,"rarity":"Uncommon","seed":77,"watermarkDay":"","watermarkTokens":0,"graduated":[],"pathResolved":true,"isEgg":true}""");

        var loaded = new CompanionStore(directory.File).Load();

        Assert.True(loaded.HasCompanion);
        Assert.Equal(133, loaded.ToCompanion().CurrentSpeciesId);
    }

    [Fact]
    public void APrePokedexSaveIsBackfilledFromWhatItAlreadyProves()
    {
        // Showing an established player an empty Pokédex reads as lost progress. Stage 1 of
        // [133, 134] means both forms were reached; 3 was carried to the end earlier.
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"speciesPath":[133,134],"stageIndex":1,"tokensAtStage":0,"rarity":"Uncommon","seed":77,"watermarkDay":"","watermarkTokens":0,"graduated":[3],"pathResolved":true}""");

        Assert.Equal([3, 133, 134], new CompanionStore(directory.File).Load().Pokedex);
    }

    [Fact]
    public void BackfillingStopsAtTheFormActuallyReached()
    {
        // A later form is on the path but has not been grown into, so it is not owned.
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"speciesPath":[1,2,3],"stageIndex":0,"tokensAtStage":0,"rarity":"Common","seed":1,"watermarkDay":"","watermarkTokens":0,"graduated":[]}""");

        Assert.Equal([1], new CompanionStore(directory.File).Load().Pokedex);
    }

    [Fact]
    public void AnEmptyPokedexIsLeftEmptyRatherThanBackfilled()
    {
        // A new game genuinely owns nothing. Only an absent list means "written before the
        // Pokédex existed", which is why the field is nullable.
        var state = GameStates.WithCompanion() with { Pokedex = [] };

        Assert.Empty(state.Sanitized().Pokedex!);
    }

    [Fact]
    public void ASaveWithNoCompanionAndNoOfferIsGivenAnOffer()
    {
        // Nothing to spend on and nothing to choose is a dead end, not a valid state.
        using var directory = new TempMigrationDirectory();
        File.WriteAllText(
            directory.File,
            """{"version":1,"speciesPath":[],"stageIndex":0,"tokensAtStage":0,"budget":9000000,"rarity":"Common","seed":5,"watermarkDay":"","watermarkTokens":0,"graduated":[]}""");

        var loaded = new CompanionStore(directory.File).Load();

        Assert.Equal(CompanionEconomy.OfferSize, loaded.OfferSeeds!.Count);
        Assert.Equal(9_000_000, loaded.Available);
    }

    private sealed class TempMigrationDirectory : IDisposable
    {
        public TempMigrationDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "ptb-migrate-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Root);
            File = Path.Combine(Root, "companion.json");
        }

        public string Root { get; }

        public string File { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
