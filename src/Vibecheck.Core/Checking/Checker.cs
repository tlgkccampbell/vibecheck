using System.Text.RegularExpressions;
using Vibecheck.Analysis;
using Vibecheck.Format;
using Vibecheck.Model;

namespace Vibecheck.Checking;

/// <summary>Inputs to a check beyond the map and the analysis.</summary>
public sealed record CheckOptions
{
    /// <summary>The commit the analysis describes, recorded in the facts.</summary>
    public string? Commit { get; init; }

    /// <summary>The map file's text as read from disk, for the canonical-form warning. Absent skips that check.</summary>
    public string? MapText { get; init; }

    /// <summary>The layout file's text, when one exists, for the shape check.</summary>
    public string? LayoutText { get; init; }
}

/// <summary>
/// Applies a map to an analysis, as <c>docs/vibecheck-spec.md</c> section 4 says, and
/// produces the facts document: every type's leaf, every dependency as evidence and
/// aggregated per pair of leaves, and the metrics per node, level and view.
/// </summary>
public static class Checker
{
    public static Facts Run(ArchitectureMap map, AnalysisResult analysis, CheckOptions? options = null)
    {
        options ??= new CheckOptions();
        var violations = new List<ViolationFact>();
        var warnings = new List<string>(analysis.Warnings);

        var leaves = CheckWellFormedness(map, options, violations, warnings);
        var exclusion = ParseExclusion(map, violations);
        var (included, excluded) = exclusion.Apply(analysis);
        foreach (var type in analysis.Types.Where(t => t.Attribute is not null && exclusion.Excludes(t)))
            warnings.Add($"{type.Name} carries [Vibecheck(\"{type.Attribute}\")] but the map excludes it");
        foreach (var type in analysis.Types.Where(t => t.Attribute is not null && exclusion.IsTest(t)))
            warnings.Add($"{type.Name} carries [Vibecheck(\"{type.Attribute}\")] but the map lists it as a test type");
        warnings.AddRange(MovableExclusions(map, analysis));
        var whole = analysis;
        analysis = included;
        if (violations.Count > 0)
        {
            return new Facts
            {
                Commit = options.Commit,
                Map = map,
                Excluded = excluded,
                Types = [],
                Files = [],
                Dependencies = [],
                LeafEdges = [],
                ExternalRefs = [],
                Unresolvable = [],
                Nodes = new SortedDictionary<string, NodeMetrics>(StringComparer.Ordinal),
                Levels = new SortedDictionary<string, LevelMetrics>(StringComparer.Ordinal),
                Views = new SortedDictionary<string, ViewMetrics>(StringComparer.Ordinal),
                Violations = violations,
                Warnings = warnings,
            };
        }

        var (leafOf, boundBy) = Partition(leaves, analysis, warnings);

        var known = analysis.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var unlisted = analysis.Dependencies.Count(d => !known.Contains(d.FromType) || !known.Contains(d.ToType));
        if (unlisted > 0) warnings.Add($"analysis bug: {unlisted} dependencies reference types the analysis did not list and were ignored");

        var dependencies = analysis.Dependencies
            .Where(d => known.Contains(d.FromType) && known.Contains(d.ToType))
            .Select(d => new DependencyFact(d.FromType, d.ToType, d.Kind, d.Symbol, d.File, d.Line))
            .ToList();
        var externalRefs = ResolveExternals(map, analysis, warnings);
        var leafEdges = LeafEdges(dependencies, externalRefs, leafOf);

        var leavesOfFile = LeavesOfFiles(analysis, leafOf);
        var nodes = NodeMetrics(map, analysis, leafEdges, leafOf, boundBy);
        var (tests, testFiles) = TestTypes.Build(whole, exclusion, analysis, leafOf);
        var types = analysis.Types.Select(t => new TypeFact(t.Name, t.Assembly, leafOf[t.Name], boundBy[t.Name], t.Files, t.Generator, t.Traits)).ToList();
        var generated = analysis.Files.Where(f => f.Generator is not null).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var patterns = PatternDiscovery.Discover(map, types, generated, warnings);
        var facts = new Facts
        {
            Commit = options.Commit,
            Map = map,
            Excluded = excluded,
            Types = types,
            Files = analysis.Files.Select(f => new FileFact(f.Path, f.Hash, leavesOfFile.GetValueOrDefault(f.Path, []), f.Generator))
                .Concat(testFiles).OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            Dependencies = dependencies,
            LeafEdges = leafEdges,
            ExternalRefs = externalRefs,
            Unresolvable = analysis.Unresolvable.Select(u => new UnresolvableFact(u.FromType, u.Symbol, u.Reason, u.File, u.Line)).ToList(),
            Nodes = nodes,
            Levels = LevelMetrics(map, leafEdges, nodes.Keys),
            Views = ViewMetrics(map, analysis, leafEdges, leafOf, boundBy),
            Violations = violations,
            Warnings = warnings,
            Declarations = analysis.Declarations.Where(d => known.Contains(d.Type)).ToList(),
            Sites = analysis.Sites.Where(s => known.Contains(s.FromType) && known.Contains(s.ToType)).ToList(),
            Tests = tests,
            Patterns = patterns,
        };
        return facts with { Patterns = PatternFeatures.WithNorms(facts) };
    }

    /// <summary>
    /// Returns a warning for each exclude pattern that matches types of test projects alone, as
    /// <see cref="TestFrameworks.TestAssemblies"/> finds them, and that the tests list could therefore hold instead.
    /// </summary>
    private static IEnumerable<string> MovableExclusions(ArchitectureMap map, AnalysisResult analysis)
    {
        var testAssemblies = TestFrameworks.TestAssemblies(analysis);
        if (testAssemblies.Count == 0) yield break;
        foreach (var text in map.Exclude)
        {
            if (!TypePattern.TryParse(text, out var pattern, out _)) continue;
            var matched = analysis.Types.Where(t => pattern!.Matches(t.PlainName, t.Assembly)).ToList();
            if (matched.Count > 0 && matched.All(t => testAssemblies.Contains(t.Assembly)))
                yield return $"exclude pattern \"{text}\" matches only test projects; move it to \"tests\" so that reviews show what changes do to their tests";
        }
    }

    // ---------- Well-formedness ----------

    /// <summary>Checks the map's own shape, failing on anything that makes it unusable, and returns each leaf's parsed patterns.</summary>
    private static Dictionary<string, List<TypePattern>> CheckWellFormedness(ArchitectureMap map, CheckOptions options, List<ViolationFact> violations, List<string> warnings)
    {
        void Fail(string message) => violations.Add(new ViolationFact("well-formedness", message));

        if (options.MapText is not null && options.MapText != MapWriter.Write(map))
            warnings.Add("the map file is not in canonical form; run `vibecheck fmt`");

        var leaves = new Dictionary<string, List<TypePattern>>(StringComparer.Ordinal);
        foreach (var (id, node) in map.Nodes)
        {
            if (!Identifiers.IsValid(id)) Fail($"\"{id}\" is not a valid identifier");
            if (id == ArchitectureMap.Unsorted || id.StartsWith(ArchitectureMap.Unsorted + ".", StringComparison.Ordinal)) Fail($"\"{id}\" is reserved for the automatic unsorted leaf");
            var parent = Identifiers.ParentOf(id);
            if (parent is not null)
            {
                if (!map.Nodes.TryGetValue(parent, out var p)) Fail($"nodes.{id}: parent \"{parent}\" does not exist");
                else if (p.IsLeaf) Fail($"nodes.{id}: parent \"{parent}\" is a leaf and cannot have children");
            }
            if (map.Externals.ContainsKey(id)) Fail($"\"{id}\" is both a node and an external");
            if (!node.IsLeaf && !map.Nodes.Keys.Any(k => Identifiers.ParentOf(k) == id))
                Fail($"nodes.{id} is a container with no children; give it children or a bind");
            if (node.IsLeaf)
            {
                var patterns = new List<TypePattern>();
                foreach (var text in node.Bind!)
                {
                    if (TypePattern.TryParse(text, out var pattern, out var error)) patterns.Add(pattern!);
                    else Fail($"nodes.{id}: {error}");
                }
                leaves[id] = patterns;
            }
        }
        foreach (var id in map.Externals.Keys)
        {
            if (!Identifiers.IsValid(id) || id.Contains('.')) Fail($"externals.{id}: an external identifier is a single segment");
            if (id == ArchitectureMap.Unsorted) Fail($"externals.{id}: the identifier is reserved for the automatic unsorted leaf");
        }
        foreach (var (id, view) in map.Views)
        {
            if (!map.IsContainer(view.Level)) { Fail($"views.{id}: level \"{view.Level}\" is not root or a container"); continue; }
            var expected = map.LeavesUnder(view.Level).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (gid, group) in view.Groups)
            {
                foreach (var leaf in group.Leaves)
                {
                    if (!expected.Contains(leaf)) Fail($"views.{id}.groups.{gid}: \"{leaf}\" is not a leaf under {view.Level}");
                    else if (!seen.Add(leaf)) Fail($"views.{id}: leaf \"{leaf}\" appears in more than one group");
                }
            }
            foreach (var missing in expected.Except(seen).OrderBy(l => l, StringComparer.Ordinal))
                Fail($"views.{id}: leaf \"{missing}\" under {view.Level} appears in no group");
        }

        if (options.LayoutText is not null)
        {
            var errors = new List<string>();
            var layout = LayoutFile.Read(options.LayoutText, errors);
            foreach (var err in errors) Fail(err);
            if (layout is not null)
            {
                foreach (var unknown in layout.UnknownIdentifiers(map).Distinct())
                    warnings.Add($"layout mentions \"{unknown}\", which the map does not define");
            }
        }
        return leaves;
    }

    /// <summary>Parses the map's exclude list, failing for each malformed pattern.</summary>
    private static Exclusion ParseExclusion(ArchitectureMap map, List<ViolationFact> violations)
    {
        var errors = new List<string>();
        var exclusion = Exclusion.Parse(map, errors);
        foreach (var error in errors) violations.Add(new ViolationFact("well-formedness", error));
        return exclusion;
    }

    // ---------- Partition ----------

    /// <summary>
    /// Binds every type to a leaf: by attribute, then by most specific pattern, then
    /// to the unsorted leaf. Nothing here fails; each departure from a clean binding
    /// is a warning.
    /// </summary>
    private static (Dictionary<string, string> LeafOf, Dictionary<string, string> BoundBy) Partition(Dictionary<string, List<TypePattern>> leaves, AnalysisResult analysis, List<string> warnings)
    {
        var leafOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var boundBy = new Dictionary<string, string>(StringComparer.Ordinal);
        var unsorted = new List<string>();
        foreach (var type in analysis.Types)
        {
            if (type.MisattributedNested is not null)
                warnings.Add($"{type.Name}: nested type {type.MisattributedNested} carries [Vibecheck], which is ignored because nested types follow their container");
            if (type.Attribute is not null)
            {
                if (leaves.ContainsKey(type.Attribute))
                {
                    leafOf[type.Name] = type.Attribute;
                    boundBy[type.Name] = "attribute";
                    continue;
                }
                warnings.Add($"{type.Name}: [Vibecheck(\"{type.Attribute}\")] names no leaf of the map and is ignored");
            }
            var best = -1;
            var hits = new List<(string Leaf, TypePattern Pattern)>();
            foreach (var (leaf, patterns) in leaves)
            {
                foreach (var pattern in patterns)
                {
                    if (!pattern.Matches(type.PlainName, type.Assembly)) continue;
                    if (pattern.Specificity > best) { best = pattern.Specificity; hits.Clear(); }
                    if (pattern.Specificity == best && !hits.Any(h => h.Leaf == leaf)) hits.Add((leaf, pattern));
                }
            }
            if (hits.Count == 0)
            {
                leafOf[type.Name] = ArchitectureMap.Unsorted;
                boundBy[type.Name] = "fallback";
                unsorted.Add(type.Name);
                continue;
            }
            hits.Sort((a, b) => string.CompareOrdinal(a.Leaf, b.Leaf));
            if (hits.Count > 1)
            {
                var competing = string.Join(", ", hits.Select(h => $"{h.Leaf} ({h.Pattern.Text})"));
                warnings.Add($"{type.Name} is claimed equally by {competing}; {hits[0].Leaf} takes it");
            }
            leafOf[type.Name] = hits[0].Leaf;
            boundBy[type.Name] = "pattern";
        }
        if (unsorted.Count > 0)
        {
            var examples = string.Join(", ", unsorted.Take(5));
            warnings.Add($"{unsorted.Count} type(s) are unsorted: no leaf binds {examples}{(unsorted.Count > 5 ? ", …" : "")}");
        }
        return (leafOf, boundBy);
    }

    /// <summary>Maps each file to the sorted leaves of the types it declares.</summary>
    private static Dictionary<string, IReadOnlyList<string>> LeavesOfFiles(AnalysisResult analysis, Dictionary<string, string> leafOf)
    {
        var sets = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var type in analysis.Types)
        {
            foreach (var file in type.Files)
            {
                if (!sets.TryGetValue(file, out var set)) sets[file] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(leafOf[type.Name]);
            }
        }
        return sets.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.Ordinal);
    }

    // ---------- Edges ----------

    /// <summary>
    /// Resolves each external reference to the external whose pattern matches its
    /// assembly, aggregated per type and referenced type. When several patterns match, the
    /// one with the longest literal part wins, so that <c>System.Text.Json</c> beats
    /// <c>System.*</c>. An assembly no external binds is warned about once.
    /// </summary>
    private static List<ExternalRefFact> ResolveExternals(ArchitectureMap map, AnalysisResult analysis, List<string> warnings)
    {
        var patterns = map.Externals
            .SelectMany(kv => kv.Value.Bind.Select(p => (Id: kv.Key, Regex: AssemblyPattern(p), Specificity: p.Replace("*", "").Length)))
            .OrderByDescending(p => p.Specificity).ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var result = new List<ExternalRefFact>();
        foreach (var group in analysis.ExternalReferences.GroupBy(e => (e.FromType, e.Assembly, e.Type)))
        {
            var (fromType, assembly, type) = group.Key;
            if (!cache.TryGetValue(assembly, out var external))
            {
                external = patterns.FirstOrDefault(p => p.Regex.IsMatch(assembly)).Id;
                cache[assembly] = external;
            }
            var first = group.First();
            result.Add(new ExternalRefFact(fromType, assembly, type, first.Kind, first.Symbol, first.File, first.Line, group.Count(), external));
        }
        foreach (var assembly in cache.Where(kv => kv.Value is null).Select(kv => kv.Key).OrderBy(a => a, StringComparer.Ordinal))
        {
            var count = result.Where(e => e.Assembly == assembly).Sum(e => e.Count);
            warnings.Add($"assembly {assembly} is referenced {count} time(s) but no external binds it");
        }
        return result.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.Assembly, StringComparer.Ordinal).ThenBy(e => e.Type, StringComparer.Ordinal).ToList();
    }

    private static Regex AssemblyPattern(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);

    /// <summary>Aggregates dependencies per pair of leaves, and bound external references per leaf and external. A pair whose ends are one leaf is kept, since it is the leaf's internal count.</summary>
    internal static List<EdgeCount> LeafEdges(List<DependencyFact> dependencies, List<ExternalRefFact> externalRefs, Dictionary<string, string> leafOf)
    {
        var counts = new Dictionary<(string, string), int>();
        foreach (var d in dependencies)
        {
            var key = (leafOf[d.From], leafOf[d.To]);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        foreach (var e in externalRefs.Where(e => e.External is not null))
        {
            var key = (leafOf[e.From], e.External!);
            counts[key] = counts.GetValueOrDefault(key) + e.Count;
        }
        return counts.Select(kv => new EdgeCount(kv.Key.Item1, kv.Key.Item2, kv.Value))
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
    }

    // ---------- Metrics ----------

    private static SortedDictionary<string, NodeMetrics> NodeMetrics(ArchitectureMap map, AnalysisResult analysis, List<EdgeCount> leafEdges,
        Dictionary<string, string> leafOf, Dictionary<string, string> boundBy)
    {
        var ids = map.Nodes.Keys.ToList();
        if (leafOf.Values.Contains(ArchitectureMap.Unsorted)) ids.Add(ArchitectureMap.Unsorted);
        var metrics = new SortedDictionary<string, NodeMetrics>(StringComparer.Ordinal);
        foreach (var id in ids) metrics[id] = MetricsOf(l => Identifiers.IsUnder(l, id), leafEdges, leafOf, boundBy);
        return metrics;
    }

    /// <summary>Computes the metrics of the set of leaves a predicate selects, from the leaf-pair edges.</summary>
    private static NodeMetrics MetricsOf(Func<string, bool> inside, List<EdgeCount> leafEdges, Dictionary<string, string> leafOf, Dictionary<string, string> boundBy)
    {
        int internalCount = 0, crossing = 0;
        foreach (var e in leafEdges)
        {
            var a = inside(e.From);
            var b = inside(e.To);
            if (a && b) internalCount += e.Count;
            else if (a || b) crossing += e.Count;
        }
        var types = leafOf.Count(kv => inside(kv.Value));
        var attributed = boundBy.Count(kv => kv.Value == "attribute" && inside(leafOf[kv.Key]));
        return new NodeMetrics(types, internalCount, crossing, attributed);
    }

    /// <summary>Lifts the leaf-pair edges to every level and finds the cycles among each level's children.</summary>
    internal static SortedDictionary<string, LevelMetrics> LevelMetrics(ArchitectureMap map, List<EdgeCount> leafEdges, IEnumerable<string> nodeIds)
    {
        var levels = new SortedDictionary<string, LevelMetrics>(StringComparer.Ordinal);
        var lifted = new Dictionary<string, Dictionary<(string, string), int>>(StringComparer.Ordinal);
        foreach (var e in leafEdges)
        {
            if (e.From == e.To) continue;
            var (level, from, to) = Identifiers.Lift(e.From, e.To);
            if (!lifted.TryGetValue(level, out var edges)) lifted[level] = edges = [];
            edges[(from, to)] = edges.GetValueOrDefault((from, to)) + e.Count;
        }
        var containers = new[] { Identifiers.Root }.Concat(map.Nodes.Where(kv => !kv.Value.IsLeaf).Select(kv => kv.Key));
        foreach (var level in containers)
        {
            var edges = lifted.GetValueOrDefault(level) ?? [];
            var list = edges.Select(kv => new EdgeCount(kv.Key.Item1, kv.Key.Item2, kv.Value))
                .OrderByDescending(e => e.Count).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
            var children = map.ChildrenOf(level).ToList();
            if (level == Identifiers.Root && nodeIds.Contains(ArchitectureMap.Unsorted)) children.Add(ArchitectureMap.Unsorted);
            levels[level] = new LevelMetrics(list, Cycles(children, list));
        }
        return levels;
    }

    private static SortedDictionary<string, ViewMetrics> ViewMetrics(ArchitectureMap map, AnalysisResult analysis, List<EdgeCount> leafEdges,
        Dictionary<string, string> leafOf, Dictionary<string, string> boundBy)
    {
        var views = new SortedDictionary<string, ViewMetrics>(StringComparer.Ordinal);
        foreach (var (id, view) in map.Views)
        {
            var groupOf = view.Groups.SelectMany(g => g.Value.Leaves.Select(l => (Leaf: l, Group: g.Key))).ToDictionary(x => x.Leaf, x => x.Group, StringComparer.Ordinal);
            var groups = new SortedDictionary<string, NodeMetrics>(StringComparer.Ordinal);
            foreach (var gid in view.Groups.Keys)
                groups[gid] = MetricsOf(l => groupOf.GetValueOrDefault(l) == gid, leafEdges, leafOf, boundBy);
            var edges = new Dictionary<(string, string), int>();
            foreach (var e in leafEdges)
            {
                if (!groupOf.TryGetValue(e.From, out var a) || !groupOf.TryGetValue(e.To, out var b) || a == b) continue;
                edges[(a, b)] = edges.GetValueOrDefault((a, b)) + e.Count;
            }
            var list = edges.Select(kv => new EdgeCount(kv.Key.Item1, kv.Key.Item2, kv.Value))
                .OrderByDescending(e => e.Count).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
            views[id] = new ViewMetrics(groups, list, Cycles(view.Groups.Keys.ToList(), list));
        }
        return views;
    }

    /// <summary>
    /// Returns the strongly connected components with more than one member among some nodes, each with its members in
    /// ordinal order. A component is a set, not a path: two members listed next to each other need not share an edge.
    /// <see cref="CutSet"/> says which edges hold a component together.
    /// </summary>
    internal static List<List<string>> Cycles(List<string> nodes, List<EdgeCount> edges)
    {
        var adjacency = nodes.ToDictionary(c => c, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (adjacency.ContainsKey(e.From) && adjacency.ContainsKey(e.To)) adjacency[e.From].Add(e.To);
        }
        return Tarjan(nodes, adjacency).Select(c => c.Order(StringComparer.Ordinal).ToList()).ToList();
    }

    /// <summary>The most members a component may have for <see cref="CutSet"/> to find its lightest cut exactly.</summary>
    private const int ExactCut = 16;

    /// <summary>
    /// Returns the edges among a cycle's members whose removal leaves no cycle among them, lightest first. This answers
    /// which references a map must stop separating to break a cycle. For a component of at most 16 members the set is
    /// the one with the fewest references. A larger component's set comes from the greedy ordering of Eades, Lin and
    /// Smyth, weighted by reference counts, and is then made minimal, so that no edge in it can be kept without a cycle
    /// coming back.
    /// </summary>
    /// <param name="members">The members of one component that <see cref="Cycles"/> returned.</param>
    /// <param name="edges">The edges among the level's or view's children, with their reference counts.</param>
    public static List<EdgeCount> CutSet(IReadOnlyList<string> members, IEnumerable<EdgeCount> edges)
    {
        var set = members.ToHashSet(StringComparer.Ordinal);
        var inside = edges.Where(e => e.From != e.To && set.Contains(e.From) && set.Contains(e.To)).ToList();
        var order = members.Count <= ExactCut ? LightestOrder(members, inside) : GreedyOrder(members, inside);

        // The edges that point backward in the order are the ones that close the cycles.
        var position = order.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        var cut = inside.Where(e => position[e.From] > position[e.To]).ToList();

        // An edge whose return leaves no cycle was not needed in the cut. The heaviest are tried first, so that they stay.
        foreach (var e in cut.OrderByDescending(e => e.Count).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList())
        {
            var kept = inside.Where(x => x == e || !cut.Contains(x)).ToList();
            if (Cycles(members.ToList(), kept).Count == 0) cut.Remove(e);
        }
        return cut.OrderBy(e => e.Count).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns the order of some nodes whose backward edges carry the fewest references, by dynamic programming over
    /// the sets of nodes placed first. Ties go to the order that places the node whose name sorts first earlier.
    /// </summary>
    private static List<string> LightestOrder(IReadOnlyList<string> members, List<EdgeCount> edges)
    {
        var nodes = members.Order(StringComparer.Ordinal).ToList();
        var n = nodes.Count;
        var index = nodes.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i, StringComparer.Ordinal);
        var weight = new int[n, n];
        foreach (var e in edges) weight[index[e.From], index[e.To]] += e.Count;

        // best[s] is the least weight of backward edges among the nodes of s, placed first in some order, and last[s] is
        // the node that order places last. Placing v after s makes each edge from v into s point backward.
        var best = new int[1 << n];
        var last = new int[1 << n];
        Array.Fill(best, int.MaxValue);
        best[0] = 0;
        for (var s = 0; s < 1 << n; s++)
        {
            if (best[s] == int.MaxValue) continue;
            for (var v = 0; v < n; v++)
            {
                if ((s & (1 << v)) != 0) continue;
                var backward = 0;
                for (var u = 0; u < n; u++)
                {
                    if ((s & (1 << u)) != 0) backward += weight[v, u];
                }
                var t = s | (1 << v);
                if (best[s] + backward < best[t]) (best[t], last[t]) = (best[s] + backward, v);
            }
        }
        var order = new List<string>();
        for (var s = (1 << n) - 1; s != 0; s &= ~(1 << last[s])) order.Insert(0, nodes[last[s]]);
        return order;
    }

    /// <summary>
    /// Returns an order of some nodes with few references on its backward edges, by the greedy ordering of Eades, Lin and
    /// Smyth. Sinks go to the end of the order and sources to the front. Otherwise, the node whose outgoing references
    /// most outweigh its incoming ones goes to the front.
    /// </summary>
    private static List<string> GreedyOrder(IReadOnlyList<string> members, List<EdgeCount> edges)
    {
        var remaining = members.Order(StringComparer.Ordinal).ToList();
        var front = new List<string>();
        var back = new List<string>();
        int Weight(string node, bool outgoing) => edges.Where(e => (outgoing ? e.From : e.To) == node && remaining.Contains(outgoing ? e.To : e.From)).Sum(e => e.Count);
        while (remaining.Count > 0)
        {
            if (remaining.FirstOrDefault(n => Weight(n, outgoing: true) == 0) is { } sink) { back.Insert(0, sink); remaining.Remove(sink); continue; }
            if (remaining.FirstOrDefault(n => Weight(n, outgoing: false) == 0) is { } source) { front.Add(source); remaining.Remove(source); continue; }
            var chosen = remaining.OrderByDescending(n => Weight(n, outgoing: true) - Weight(n, outgoing: false)).First();
            front.Add(chosen);
            remaining.Remove(chosen);
        }
        return [.. front, .. back];
    }

    private static List<List<string>> Tarjan(List<string> nodes, Dictionary<string, List<string>> adjacency)
    {
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlink = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<List<string>>();

        // The search keeps its own stack of nodes being visited, each with the position of the next successor to
        // visit, since a type graph can hold chains far deeper than the call stack allows.
        var visiting = new Stack<(string Node, int Next)>();
        void Enter(string v)
        {
            indices[v] = lowlink[v] = index++;
            stack.Push(v);
            onStack.Add(v);
            visiting.Push((v, 0));
        }

        foreach (var root in nodes)
        {
            if (indices.ContainsKey(root)) continue;
            Enter(root);
            while (visiting.Count > 0)
            {
                var (v, next) = visiting.Pop();
                var successors = adjacency[v];
                if (next < successors.Count)
                {
                    visiting.Push((v, next + 1));
                    var w = successors[next];
                    if (!indices.ContainsKey(w)) Enter(w);
                    else if (onStack.Contains(w)) lowlink[v] = Math.Min(lowlink[v], indices[w]);
                    continue;
                }
                if (visiting.Count > 0)
                {
                    var parent = visiting.Peek().Node;
                    lowlink[parent] = Math.Min(lowlink[parent], lowlink[v]);
                }
                if (lowlink[v] == indices[v])
                {
                    var component = new List<string>();
                    string w;
                    do { w = stack.Pop(); onStack.Remove(w); component.Add(w); } while (w != v);
                    if (component.Count > 1) { component.Reverse(); result.Add(component); }
                }
            }
        }
        return result;
    }
}
