namespace PokeTokenBar.Core.Companions;

/// <summary>An evolution line a companion can be drawn from.</summary>
public sealed record EvolutionLine
{
    public required IReadOnlyList<int> SpeciesPath { get; init; }

    public required Rarity Rarity { get; init; }

    /// <summary>
    /// True when the path came from a real evolution chain. False means the chain could not be
    /// fetched, so the path may be truncated and is worth re-resolving later — a transient
    /// network failure must not permanently mislabel a three-form species as single-form.
    /// </summary>
    public bool Resolved { get; init; } = true;
}

/// <summary>
/// Built-in evolution lines.
/// </summary>
/// <remarks>
/// A stopgap. The original derives lines from PokéAPI's evolution-chain endpoint, which is the
/// right answer and covers every species; this fixed set exists so a companion can be hatched
/// with no network at all and the rest of the feature can be built and tested offline.
/// Replacing it means fetching chains in the sidecar and caching them like sprites.
/// </remarks>
public static class EvolutionLines
{
    public static IReadOnlyList<EvolutionLine> All { get; } =
    [
        new() { SpeciesPath = [1, 2, 3], Rarity = Rarity.Common },
        new() { SpeciesPath = [4, 5, 6], Rarity = Rarity.Common },
        new() { SpeciesPath = [7, 8, 9], Rarity = Rarity.Common },
        new() { SpeciesPath = [10, 11, 12], Rarity = Rarity.Common },
        new() { SpeciesPath = [172, 25, 26], Rarity = Rarity.Uncommon },
        new() { SpeciesPath = [133, 134], Rarity = Rarity.Uncommon },
        new() { SpeciesPath = [147, 148, 149], Rarity = Rarity.Rare },
        new() { SpeciesPath = [150], Rarity = Rarity.Legendary },
    ];

    /// <summary>
    /// Picks a line from a seed. Deterministic on purpose: the seed is persisted at hatch, so a
    /// companion survives a restart instead of being redrawn into a different species.
    /// </summary>
    /// <param name="seed">The egg's seed.</param>
    /// <param name="completed">
    /// Final forms of completed lines. Lines ending in one are skipped until every line has
    /// been completed, after which any may repeat.
    /// </param>
    public static EvolutionLine FromSeed(int seed, IReadOnlyCollection<int> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        var weights = All
            .Select(line => completed.Contains(line.SpeciesPath[^1]) ? 0 : Weight(line.Rarity))
            .ToArray();
        if (weights.Sum() == 0)
        {
            weights = [.. All.Select(static line => Weight(line.Rarity))];
        }

        var total = weights.Sum();
        var point = (int)((uint)seed % (uint)total);

        for (var i = 0; i < All.Count; i++)
        {
            point -= weights[i];
            if (point < 0)
            {
                return All[i];
            }
        }

        return All[0];
    }

    /// <summary>Rarer lines are drawn less often. Relative, not a real capture-rate model.</summary>
    private static int Weight(Rarity rarity) => rarity switch
    {
        Rarity.Common => 40,
        Rarity.Uncommon => 12,
        Rarity.Rare => 4,
        Rarity.Legendary => 1,
        _ => 40,
    };
}
