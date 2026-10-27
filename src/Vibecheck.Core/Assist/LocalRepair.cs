using System.Diagnostics;
using Vibecheck.Bootstrap;
using Vibecheck.Checking;
using Vibecheck.Model;

namespace Vibecheck.Assist;

/// <summary>Holds a map that the checker has accepted: the map as the reader reads it back, its canonical text, and the checker's facts about it.</summary>
internal sealed record CheckedMap(ArchitectureMap Map, string Text, Facts Facts);

/// <summary>Holds one type that local repair moved, or that the fit guard held in place, and the cycle it was for.</summary>
/// <param name="Type">The type's full name.</param>
/// <param name="Leaf">The leaf the type moved into, or, for a type held in place, the leaf it stays in.</param>
/// <param name="Level">The level whose children the cycle is among.</param>
internal sealed record RepairMove(string Type, string Leaf, string Level);

/// <summary>Holds what one run of local repair did.</summary>
/// <param name="Map">The map after every candidate that was kept.</param>
/// <param name="Moves">The types moved, in the order they moved.</param>
/// <param name="Held">The types that the fit guard held in place.</param>
/// <param name="Repaired">The number of cycles broken.</param>
/// <param name="Elapsed">How long the search took.</param>
/// <param name="OutOfTime">A value indicating whether the search stopped at its budget.</param>
/// <param name="Left">Each cycle among a level's children that is not in the code and that the search left, by the key that <see cref="LocalRepair.Key"/> returns, with why it was left.</param>
internal sealed record RepairOutcome(CheckedMap Map, IReadOnlyList<RepairMove> Moves, IReadOnlyList<RepairMove> Held, int Repaired, TimeSpan Elapsed, bool OutOfTime,
    IReadOnlyList<(string Cycle, string Why)> Left);

/// <summary>
/// Breaks cycles among a level's children by moving the few types behind them, and checks each candidate with the
/// checker before it keeps it. A cycle is <b>repairable</b> when its cut set has at most
/// <see cref="MapGenerator.RepairEdges"/> edges and the cheapest candidate moves at most
/// <see cref="MapGenerator.RepairTypes"/> types. A cycle that is neither repairable nor in the code is <b>entangled</b>: its
/// members reference each other in so many ways that a change of grouping, usually a merge, is the fix. See
/// <c>docs/map-cycles.md</c> section 3.
/// </summary>
internal sealed class LocalRepair
{
    private readonly Dictionary<string, IReadOnlyList<string>> unitOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Knot> knotOf = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<Knot> knots;
    private readonly TimeSpan budget;

    /// <summary>Creates a search over a solution's units and knots that stops after a budget.</summary>
    /// <param name="units">The solution's units. A type moves with the members of its unit that share its leaf.</param>
    /// <param name="knots">The solution's knots. A type moves with the members of its small knot that share its leaf.</param>
    /// <param name="budget">How long one run of the search may take.</param>
    public LocalRepair(IEnumerable<Unit> units, IReadOnlyList<Knot> knots, TimeSpan budget)
    {
        foreach (var unit in units)
        {
            foreach (var m in unit.Members) unitOf[m] = unit.Members;
        }
        foreach (var knot in knots.Where(k => k.Small))
        {
            foreach (var t in knot.Types) knotOf[t] = knot;
        }
        (this.knots, this.budget) = (knots, budget);
    }

    /// <summary>
    /// Breaks the repairable cycles of a map, lightest cut set first, and returns the map with every candidate that
    /// broke its cycle without raising the number of findings. Each cycle is tried once, with at most one candidate per
    /// edge of its cut set and one more, and each kept candidate is the map the next cycle starts from. Also returns
    /// why each cycle that remains was left: entangled, an edge that no move clears, an edge whose sides may not move,
    /// or what each candidate did.
    /// </summary>
    /// <param name="start">The map to repair.</param>
    /// <param name="check">A function that checks a candidate map, and returns <see langword="null"/> when it cannot be used.</param>
    /// <param name="score">A function that returns the number of findings a map can fix.</param>
    public RepairOutcome Run(CheckedMap start, Func<ArchitectureMap, CheckedMap?> check, Func<Facts, int> score)
    {
        var watch = Stopwatch.StartNew();
        var (current, best) = (start, score(start.Facts));
        var moves = new List<RepairMove>();
        var held = new List<RepairMove>();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var why = new Dictionary<string, string>(StringComparer.Ordinal);
        var (repaired, outOfTime) = (0, false);
        while (!outOfTime)
        {
            var plan = Plans(current.Facts).Where(p => p.Edges is not null && !p.Entangled && !tried.Contains(p.Key))
                .OrderBy(p => p.Cut.Sum(e => e.Count)).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault();
            if (plan is null) break;
            tried.Add(plan.Key);
            held.AddRange(plan.Edges!.SelectMany(e => new[] { e.Source, e.Target }).Where(s => s.Held is not null).Select(s => s.Held!)
                .DistinctBy(h => h.Type).Select(h => h with { Level = plan.Level }));
            var blocked = plan.Edges!.Where(e => !e.Source.Allowed && !e.Target.Allowed).ToList();
            if (blocked.Count > 0)
            {
                why[plan.Key] = string.Join("; ", blocked.Select(e => $"neither side of {e.Edge.From} → {e.Edge.To} may move: {e.Source.Blocked}, and {e.Target.Blocked}"));
                continue;
            }
            var results = new List<string>();
            var broken = false;
            foreach (var (candidate, n) in Candidates(plan.Edges!).Select((c, i) => (c, i + 1)))
            {
                string Result(string what) => $"candidate {n} ({candidate.Count} type(s)) {what}";
                if (candidate.Count > MapGenerator.RepairTypes) { results.Add(Result($"moves more than {MapGenerator.RepairTypes} types and was not tried")); continue; }
                if (watch.Elapsed >= budget) { outOfTime = true; results.Add(Result("was not tried before the budget ran out")); break; }
                var moved = current.Map.Clone();
                foreach (var (type, leaf) in candidate) MapEdits.Move(moved, type, leaf);
                if (check(moved) is not { } after) { results.Add(Result("made the map unusable")); continue; }
                if (!Gone(after.Facts, plan.Level, plan.Members)) { results.Add(Result("left the cycle")); continue; }
                var found = score(after.Facts);
                if (found > best) { results.Add(Result($"broke the cycle but raised the findings from {best} to {found}")); continue; }
                (current, best) = (after, found);
                moves.AddRange(candidate.Select(m => new RepairMove(m.Type, m.Leaf, plan.Level)));
                repaired++;
                broken = true;
                break;
            }
            if (!broken) why[plan.Key] = string.Join("; ", results);
        }
        var left = Plans(current.Facts).Select(p => (p.Key, p.Why ?? why.GetValueOrDefault(p.Key)
            ?? (tried.Contains(p.Key) ? "a later repair brought it back" : "it was not tried before the budget ran out"))).ToList();
        return new RepairOutcome(current, moves, held, repaired, watch.Elapsed, outOfTime, left);
    }

    /// <summary>Returns a function that tells whether a cycle among a level's children is entangled.</summary>
    /// <param name="facts">The checker's facts about the map.</param>
    public Func<string, IReadOnlyList<string>, bool> Entangled(Facts facts)
    {
        var entangled = Plans(facts).Where(p => p.Entangled).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        return (level, cycle) => entangled.Contains(Key(level, cycle));
    }

    /// <summary>Returns the key that names a cycle among a level's children, as its finding's first line does.</summary>
    public static string Key(string level, IReadOnlyList<string> cycle) => $"{level}: {string.Join(", ", cycle)}";

    /// <summary>Holds one way to clear an edge: the types that move, each with the leaf it moves into.</summary>
    /// <param name="Moves">The moves, or an empty list when the side cannot move.</param>
    /// <param name="Allowed">A value indicating whether the side may move.</param>
    /// <param name="Held">The type the fit guard held in place, when it did.</param>
    /// <param name="Blocked">Why the side may not move, when it may not.</param>
    private sealed record Side(IReadOnlyList<(string Type, string Leaf)> Moves, bool Allowed, RepairMove? Held, string? Blocked);

    /// <summary>Holds the two ways to clear one edge of a cut set.</summary>
    private sealed record EdgePlan(EdgeCount Edge, Side Source, Side Target)
    {
        /// <summary>Gets the number of types the cheaper side moves, whether or not it may move.</summary>
        public int Cheapest => Math.Min(Source.Moves.Count, Target.Moves.Count);
    }

    /// <summary>Holds a cycle that is not in the code, its cut set, and the ways to clear each edge of it.</summary>
    /// <param name="Edges">The ways to clear each edge, or <see langword="null"/> when the cycle is entangled or an edge cannot be cleared by moving types.</param>
    /// <param name="Why">Why the cycle cannot be repaired, when <paramref name="Edges"/> is <see langword="null"/>.</param>
    private sealed record CyclePlan(string Level, IReadOnlyList<string> Members, List<EdgeCount> Cut, List<EdgePlan>? Edges, bool Entangled, string? Why = null)
    {
        /// <summary>Gets the key that names the cycle, as <see cref="LocalRepair.Key"/> returns it.</summary>
        public string Key => LocalRepair.Key(Level, Members);
    }

    /// <summary>Returns a plan for each cycle among a level's children that is not in the code.</summary>
    private List<CyclePlan> Plans(Facts facts)
    {
        var inCode = Knots.InCode(facts, knots);
        var index = new Index(facts);
        var plans = new List<CyclePlan>();
        foreach (var (level, metrics) in facts.Levels)
        {
            foreach (var cycle in metrics.Cycles.Where(c => !inCode(level, c)))
            {
                var cut = Checker.CutSet(cycle, metrics.Edges);
                if (cut.Count > MapGenerator.RepairEdges)
                {
                    plans.Add(new(level, cycle, cut, null, true, $"it is entangled: its cut set has {cut.Count} edges, more than {MapGenerator.RepairEdges}"));
                    continue;
                }
                var edges = cut.Select(e => Plan(facts, index, e)).OfType<EdgePlan>().ToList();
                if (edges.Count < cut.Count)
                {
                    var outside = cut.First(e => !facts.Map.Nodes.ContainsKey(e.From) || !facts.Map.Nodes.ContainsKey(e.To));
                    plans.Add(new(level, cycle, cut, null, false, $"no move clears its edge {outside.From} → {outside.To}, which leads to or from an external or the unsorted leaf"));
                    continue;
                }
                var cheapest = edges.Sum(e => e.Cheapest);
                var entangled = cheapest > MapGenerator.RepairTypes;
                plans.Add(new(level, cycle, cut, entangled ? null : edges, entangled,
                    entangled ? $"it is entangled: clearing its {cut.Count} cut edge(s) moves at least {cheapest} types, more than {MapGenerator.RepairTypes}" : null));
            }
        }
        return plans;
    }

    /// <summary>
    /// Returns one side of an edge: the types to move, with the members that move with each, and whether they may move.
    /// A side may not move a type bound by a <c>[Vibecheck]</c> attribute, a type into or out of the unsorted leaf, or
    /// a group whose affinity with its new leaf is less than its affinity with its own, which the fit guard holds in place.
    /// </summary>
    /// <param name="index">The index of the checker's facts.</param>
    /// <param name="wanted">Each type to move, with the leaf it moves into.</param>
    private Side SideOf(Index index, IEnumerable<(string Type, string Leaf)> wanted)
    {
        var moves = new List<(string Type, string Leaf)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        RepairMove? held = null;
        string? blocked = null;
        foreach (var (type, leaf) in wanted)
        {
            if (taken.Contains(type)) continue;
            var from = index.LeafOf[type];
            var group = Group(index, type);
            taken.UnionWith(group);
            moves.AddRange(group.Select(t => (t, leaf)));
            if (blocked is not null) continue;
            if (from == ArchitectureMap.Unsorted || leaf == ArchitectureMap.Unsorted) blocked = $"{type} would move into or out of the unsorted leaf";
            else if (group.FirstOrDefault(t => index.Types[t].By == "attribute") is { } bound) blocked = $"{bound} is bound by an attribute";
            else if ((index.Affinity(group, from), index.Affinity(group, leaf)) is var (own, other) && other < own)
            {
                held = new RepairMove(type, from, "");
                blocked = $"the fit guard holds {type} in {from}, with {own} reference(s) there and {other} to {leaf}";
            }
        }
        return new Side(moves, blocked is null, held, blocked);
    }

    /// <summary>
    /// Returns the two ways to clear an edge between two children, or <see langword="null"/> when an end is not a node
    /// of the map, such as an external or the unsorted leaf. The source side moves each type behind the edge's references
    /// into the leaf of the type it references most; the target side moves each referenced type into the leaf of the
    /// type that references it most.
    /// </summary>
    private EdgePlan? Plan(Facts facts, Index index, EdgeCount edge)
    {
        if (!facts.Map.Nodes.ContainsKey(edge.From) || !facts.Map.Nodes.ContainsKey(edge.To)) return null;
        var pairs = Explain.Pairs(facts, edge.From, edge.To);
        var source = SideOf(index, pairs.Select(p => (p.FromType, index.LeafOf[p.ToType])));
        var target = SideOf(index, pairs.Select(p => (p.ToType, index.LeafOf[p.FromType])));
        return new EdgePlan(edge, source, target);
    }

    /// <summary>Returns a type and the members of its unit and its small knot that share its leaf, which move with it.</summary>
    private List<string> Group(Index index, string type)
    {
        var leaf = index.LeafOf[type];
        var group = new List<string> { type };
        var seen = new HashSet<string>(StringComparer.Ordinal) { type };
        for (var i = 0; i < group.Count; i++)
        {
            var related = unitOf.GetValueOrDefault(group[i]) ?? [];
            if (knotOf.TryGetValue(group[i], out var knot)) related = [.. related, .. knot.Types];
            foreach (var r in related)
            {
                if (index.LeafOf.TryGetValue(r, out var l) && l == leaf && seen.Add(r)) group.Add(r);
            }
        }
        return group;
    }

    /// <summary>
    /// Returns the candidates for a cycle, in the order they are tried. The first clears each edge from its cheaper side
    /// that may move, the source side when they tie. Each later one clears one edge from its other side instead. There is
    /// no candidate when some edge has no side that may move.
    /// </summary>
    private static IEnumerable<List<(string Type, string Leaf)>> Candidates(List<EdgePlan> edges)
    {
        Side? Cheaper(EdgePlan e) =>
            e.Source.Allowed && (!e.Target.Allowed || e.Source.Moves.Count <= e.Target.Moves.Count) ? e.Source : e.Target.Allowed ? e.Target : null;
        var choice = edges.Select(Cheaper).OfType<Side>().ToList();
        if (choice.Count < edges.Count) yield break;
        var candidates = new List<List<Side>> { choice };
        for (var i = 0; i < edges.Count; i++)
        {
            var other = ReferenceEquals(choice[i], edges[i].Source) ? edges[i].Target : edges[i].Source;
            if (other.Allowed) candidates.Add([.. choice.Take(i), other, .. choice.Skip(i + 1)]);
        }
        foreach (var sides in candidates) yield return sides.SelectMany(s => s.Moves).DistinctBy(m => m.Type).ToList();
    }

    /// <summary>Returns a value indicating whether no cycle among a level's children still holds two of a cycle's members.</summary>
    private static bool Gone(Facts facts, string level, IReadOnlyList<string> members) =>
        !facts.Levels.TryGetValue(level, out var m) || !m.Cycles.Any(c => c.Count(members.Contains) >= 2);

    /// <summary>Indexes the checker's facts by type: each type's leaf and fact, and the types each references or is referenced by, once per reference.</summary>
    private sealed class Index
    {
        public Index(Facts facts)
        {
            Types = facts.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
            LeafOf = facts.Types.ToDictionary(t => t.Name, t => t.Leaf, StringComparer.Ordinal);
            foreach (var d in facts.Dependencies)
            {
                Add(d.From, d.To);
                Add(d.To, d.From);
            }
        }

        /// <summary>Gets each type's fact, by full name.</summary>
        public Dictionary<string, TypeFact> Types { get; }

        /// <summary>Gets each type's leaf, by full name.</summary>
        public Dictionary<string, string> LeafOf { get; }

        private Dictionary<string, List<string>> Neighbors { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Returns a group's affinity with a leaf: the number of references between the group's types and the leaf's
        /// other types, in either direction. References among the group's own types do not count.
        /// </summary>
        public int Affinity(IReadOnlyCollection<string> group, string leaf) =>
            group.Sum(t => Neighbors.GetValueOrDefault(t)?.Count(n => !group.Contains(n) && LeafOf[n] == leaf) ?? 0);

        private void Add(string from, string to)
        {
            if (!Neighbors.TryGetValue(from, out var list)) Neighbors[from] = list = [];
            list.Add(to);
        }
    }
}
