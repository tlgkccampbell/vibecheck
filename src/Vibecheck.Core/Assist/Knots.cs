using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Model;

namespace Vibecheck.Assist;

/// <summary>
/// Represents a knot: two or more types that reference each other in a loop, which is a strongly connected component
/// of the graph of references among a solution's included types. A knot whose types sit in more than one leaf shows
/// up as a cycle at the level where those leaves' branches meet, whatever the rest of the map says. See
/// <c>docs/map-cycles.md</c> section 2.
/// </summary>
/// <param name="Types">The full names of the knot's types, in ordinal order.</param>
internal sealed record Knot(IReadOnlyList<string> Types)
{
    /// <summary>Gets a value indicating whether the knot's types fit in one leaf.</summary>
    public bool Small => Types.Count <= MapGenerator.LargeLeaf;
}

/// <summary>Finds the knots of a solution and works out what they mean for a map.</summary>
internal static class Knots
{
    /// <summary>Returns the knots among an analysis's types, largest first.</summary>
    /// <param name="analysis">The analysis, with the map's exclusions and test types already left out.</param>
    public static List<Knot> Find(AnalysisResult analysis)
    {
        var names = analysis.Types.Select(t => t.Name).ToList();
        var known = names.ToHashSet(StringComparer.Ordinal);
        var edges = analysis.Dependencies
            .Where(d => d.FromType != d.ToType && known.Contains(d.FromType) && known.Contains(d.ToType))
            .Select(d => (d.FromType, d.ToType)).Distinct()
            .Select(p => new EdgeCount(p.FromType, p.ToType, 1)).ToList();
        return Checker.Cycles(names, edges)
            .Select(c => new Knot(c.Order(StringComparer.Ordinal).ToList()))
            .OrderByDescending(k => k.Types.Count).ThenBy(k => k.Types[0], StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns the moves that bring each small knot's stray types into one leaf. The leaf is the one that holds most of
    /// the knot, then the one the knot references most, then the one whose identifier sorts first. A type bound by a
    /// <c>[Vibecheck]</c> attribute never moves, so a knot that holds one gathers in that type's leaf instead.
    /// </summary>
    /// <param name="facts">The checker's facts about the map.</param>
    /// <param name="knots">The solution's knots.</param>
    public static List<(string Type, string Leaf)> Gathering(Facts facts, IReadOnlyList<Knot> knots)
    {
        var placed = facts.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var moves = new List<(string Type, string Leaf)>();
        foreach (var knot in knots.Where(k => k.Small))
        {
            var types = knot.Types.Where(placed.ContainsKey).Select(n => placed[n]).ToList();
            var attributed = types.Where(t => t.By == "attribute").ToList();
            var leaves = (attributed.Count > 0 ? attributed : types).Select(t => t.Leaf).Where(l => l != ArchitectureMap.Unsorted).ToList();
            if (leaves.Count == 0 || types.All(t => t.Leaf == leaves[0])) continue;
            var members = knot.Types.ToHashSet(StringComparer.Ordinal);
            var referenced = facts.Dependencies.Where(d => members.Contains(d.From) && !members.Contains(d.To))
                .GroupBy(d => placed[d.To].Leaf, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var leaf = leaves.GroupBy(l => l, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => referenced.GetValueOrDefault(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;
            moves.AddRange(types.Where(t => t.Leaf != leaf && t.By != "attribute").Select(t => (t.Name, leaf)));
        }
        return moves;
    }

    /// <summary>
    /// Returns a function that tells whether a cycle among a level's children is in the code. Such a cycle disappears
    /// once the references inside large knots are left out of the leaf edges, so no grouping that a leaf can hold
    /// removes it.
    /// </summary>
    /// <param name="facts">The checker's facts about the map.</param>
    /// <param name="knots">The solution's knots.</param>
    public static Func<string, IReadOnlyList<string>, bool> InCode(Facts facts, IReadOnlyList<Knot> knots)
    {
        var knotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (knot, i) in knots.Where(k => !k.Small).Select((k, i) => (k, i)))
        {
            foreach (var t in knot.Types) knotOf[t] = i;
        }
        if (knotOf.Count == 0) return (_, _) => false;
        var leafOf = facts.Types.ToDictionary(t => t.Name, t => t.Leaf, StringComparer.Ordinal);
        var kept = facts.Dependencies.Where(d => !(knotOf.TryGetValue(d.From, out var a) && knotOf.TryGetValue(d.To, out var b) && a == b)).ToList();
        var levels = Checker.LevelMetrics(facts.Map, Checker.LeafEdges(kept, [], leafOf), facts.Nodes.Keys);
        return (level, cycle) => !levels.TryGetValue(level, out var m) || !m.Cycles.Any(c => c.Any(cycle.Contains));
    }
}
