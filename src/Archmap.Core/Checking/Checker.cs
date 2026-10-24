using System.Text.RegularExpressions;
using Archmap.Analysis;
using Archmap.Format;
using Archmap.Model;

namespace Archmap.Checking;

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
/// Applies a map to an analysis, as <c>docs/archmap-spec.md</c> section 4 says, and
/// produces the facts document: every type's leaf, every dependency as evidence and
/// aggregated per pair of leaves, and the metrics per node, level and view.
/// </summary>
public static class Checker
{
    public static Facts Run(ArchMap map, AnalysisResult analysis, CheckOptions? options = null)
    {
        options ??= new CheckOptions();
        var violations = new List<ViolationFact>();
        var warnings = new List<string>(analysis.Warnings);

        var leaves = CheckWellFormedness(map, options, violations, warnings);
        var exclusion = ParseExclusion(map, violations);
        var (included, excluded) = exclusion.Apply(analysis);
        foreach (var type in analysis.Types.Where(t => t.Attribute is not null && exclusion.Excludes(t)))
            warnings.Add($"{type.Name} carries [Archmap(\"{type.Attribute}\")] but the map excludes it");
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
        return new Facts
        {
            Commit = options.Commit,
            Map = map,
            Excluded = excluded,
            Types = analysis.Types.Select(t => new TypeFact(t.Name, t.Assembly, leafOf[t.Name], boundBy[t.Name], t.Files, t.Generator)).ToList(),
            Files = analysis.Files.Select(f => new FileFact(f.Path, f.Hash, leavesOfFile.GetValueOrDefault(f.Path, []), f.Generator)).ToList(),
            Dependencies = dependencies,
            LeafEdges = leafEdges,
            ExternalRefs = externalRefs,
            Unresolvable = analysis.Unresolvable.Select(u => new UnresolvableFact(u.FromType, u.Symbol, u.Reason, u.File, u.Line)).ToList(),
            Nodes = nodes,
            Levels = LevelMetrics(map, leafEdges, nodes.Keys),
            Views = ViewMetrics(map, analysis, leafEdges, leafOf, boundBy),
            Violations = violations,
            Warnings = warnings,
        };
    }

    // ---------- Well-formedness ----------

    /// <summary>Checks the map's own shape, failing on anything that makes it unusable, and returns each leaf's parsed patterns.</summary>
    private static Dictionary<string, List<TypePattern>> CheckWellFormedness(ArchMap map, CheckOptions options, List<ViolationFact> violations, List<string> warnings)
    {
        void Fail(string message) => violations.Add(new ViolationFact("well-formedness", message));

        if (options.MapText is not null && options.MapText != MapWriter.Write(map))
            warnings.Add("the map file is not in canonical form; run `archmap fmt`");

        var leaves = new Dictionary<string, List<TypePattern>>(StringComparer.Ordinal);
        foreach (var (id, node) in map.Nodes)
        {
            if (!Identifiers.IsValid(id)) Fail($"\"{id}\" is not a valid identifier");
            if (id == ArchMap.Unsorted || id.StartsWith(ArchMap.Unsorted + ".", StringComparison.Ordinal)) Fail($"\"{id}\" is reserved for the automatic unsorted leaf");
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
            if (id == ArchMap.Unsorted) Fail($"externals.{id}: the identifier is reserved for the automatic unsorted leaf");
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
    private static Exclusion ParseExclusion(ArchMap map, List<ViolationFact> violations)
    {
        var errors = new List<string>();
        var exclusion = Exclusion.Parse(map.Exclude, errors);
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
                warnings.Add($"{type.Name}: nested type {type.MisattributedNested} carries [Archmap], which is ignored because nested types follow their container");
            if (type.Attribute is not null)
            {
                if (leaves.ContainsKey(type.Attribute))
                {
                    leafOf[type.Name] = type.Attribute;
                    boundBy[type.Name] = "attribute";
                    continue;
                }
                warnings.Add($"{type.Name}: [Archmap(\"{type.Attribute}\")] names no leaf of the map and is ignored");
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
                leafOf[type.Name] = ArchMap.Unsorted;
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
    /// assembly, aggregated per type and assembly. When several patterns match, the
    /// one with the longest literal part wins, so that <c>System.Text.Json</c> beats
    /// <c>System.*</c>. An assembly no external binds is warned about once.
    /// </summary>
    private static List<ExternalRefFact> ResolveExternals(ArchMap map, AnalysisResult analysis, List<string> warnings)
    {
        var patterns = map.Externals
            .SelectMany(kv => kv.Value.Bind.Select(p => (Id: kv.Key, Regex: AssemblyPattern(p), Specificity: p.Replace("*", "").Length)))
            .OrderByDescending(p => p.Specificity).ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var result = new List<ExternalRefFact>();
        foreach (var group in analysis.ExternalReferences.GroupBy(e => (e.FromType, e.Assembly)))
        {
            var (fromType, assembly) = group.Key;
            if (!cache.TryGetValue(assembly, out var external))
            {
                external = patterns.FirstOrDefault(p => p.Regex.IsMatch(assembly)).Id;
                cache[assembly] = external;
            }
            var first = group.First();
            result.Add(new ExternalRefFact(fromType, assembly, first.Symbol, first.File, first.Line, group.Count(), external));
        }
        foreach (var assembly in cache.Where(kv => kv.Value is null).Select(kv => kv.Key).OrderBy(a => a, StringComparer.Ordinal))
        {
            var count = result.Where(e => e.Assembly == assembly).Sum(e => e.Count);
            warnings.Add($"assembly {assembly} is referenced {count} time(s) but no external binds it");
        }
        return result.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.Assembly, StringComparer.Ordinal).ToList();
    }

    private static Regex AssemblyPattern(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);

    /// <summary>Aggregates dependencies per pair of leaves, and bound external references per leaf and external. A pair whose ends are one leaf is kept, since it is the leaf's internal count.</summary>
    private static List<EdgeCount> LeafEdges(List<DependencyFact> dependencies, List<ExternalRefFact> externalRefs, Dictionary<string, string> leafOf)
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

    private static SortedDictionary<string, NodeMetrics> NodeMetrics(ArchMap map, AnalysisResult analysis, List<EdgeCount> leafEdges,
        Dictionary<string, string> leafOf, Dictionary<string, string> boundBy)
    {
        var ids = map.Nodes.Keys.ToList();
        if (leafOf.Values.Contains(ArchMap.Unsorted)) ids.Add(ArchMap.Unsorted);
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
    private static SortedDictionary<string, LevelMetrics> LevelMetrics(ArchMap map, List<EdgeCount> leafEdges, IEnumerable<string> nodeIds)
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
            if (level == Identifiers.Root && nodeIds.Contains(ArchMap.Unsorted)) children.Add(ArchMap.Unsorted);
            levels[level] = new LevelMetrics(list, Cycles(children, list));
        }
        return levels;
    }

    private static SortedDictionary<string, ViewMetrics> ViewMetrics(ArchMap map, AnalysisResult analysis, List<EdgeCount> leafEdges,
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

    /// <summary>Returns the strongly connected components with more than one member among some nodes, each listed in a cycle order.</summary>
    internal static List<List<string>> Cycles(List<string> nodes, List<EdgeCount> edges)
    {
        var adjacency = nodes.ToDictionary(c => c, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (adjacency.ContainsKey(e.From) && adjacency.ContainsKey(e.To)) adjacency[e.From].Add(e.To);
        }
        return Tarjan(nodes, adjacency);
    }

    private static List<List<string>> Tarjan(List<string> nodes, Dictionary<string, List<string>> adjacency)
    {
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlink = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<List<string>>();

        void StrongConnect(string v)
        {
            indices[v] = lowlink[v] = index++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in adjacency[v])
            {
                if (!indices.ContainsKey(w)) { StrongConnect(w); lowlink[v] = Math.Min(lowlink[v], lowlink[w]); }
                else if (onStack.Contains(w)) lowlink[v] = Math.Min(lowlink[v], indices[w]);
            }
            if (lowlink[v] == indices[v])
            {
                var component = new List<string>();
                string w;
                do { w = stack.Pop(); onStack.Remove(w); component.Add(w); } while (w != v);
                if (component.Count > 1) { component.Reverse(); result.Add(component); }
            }
        }

        foreach (var v in nodes) if (!indices.ContainsKey(v)) StrongConnect(v);
        return result;
    }
}
