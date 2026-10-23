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

    /// <summary>The map file's text as read from disk, for the canonical-form check. Absent skips that check.</summary>
    public string? MapText { get; init; }

    /// <summary>The layout file's text, when one exists, for the shape check.</summary>
    public string? LayoutText { get; init; }
}

/// <summary>
/// Applies the conformance rules of <c>docs/archmap-spec.md</c> section 4 to a map
/// and an analysis, and produces the facts document.
/// </summary>
public static class Checker
{
    public static Facts Run(ArchMap map, AnalysisResult analysis, CheckOptions? options = null)
    {
        options ??= new CheckOptions();
        var violations = new List<ViolationFact>();
        var warnings = new List<string>(analysis.Warnings);

        var leaves = CheckWellFormedness(map, options, violations, warnings);
        if (violations.Count > 0)
        {
            return new Facts
            {
                Commit = options.Commit,
                Map = map,
                Types = analysis.Types.Select(t => new TypeFact(t.Name, t.Assembly, null, null, t.Files, t.Generator)).ToList(),
                Files = analysis.Files.Select(f => new FileFact(f.Path, f.Hash, [], f.Generator)).ToList(),
                Dependencies = [],
                ExternalRefs = [],
                Unresolvable = analysis.Unresolvable.Select(u => new UnresolvableFact(u.FromType, u.Symbol, u.Reason, u.File, u.Line)).ToList(),
                Metrics = new SortedDictionary<string, NodeMetrics>(StringComparer.Ordinal),
                Violations = violations,
                Warnings = warnings,
            };
        }

        var (leafOf, boundBy) = Partition(map, leaves, analysis, violations);

        var known = analysis.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var unlisted = analysis.Dependencies.Count(d => !known.Contains(d.FromType) || !known.Contains(d.ToType));
        if (unlisted > 0) warnings.Add($"analysis bug: {unlisted} dependencies reference types the analysis did not list and were ignored");

        var dependencies = Lift(analysis, leafOf);
        var externalRefs = ResolveExternals(map, analysis, leafOf);

        // Lifted groups: (level, from, to) -> evidence, one entry per reference. External
        // references join the root level.
        var groups = new Dictionary<(string Level, string From, string To), List<DependencyFact>>();
        foreach (var d in dependencies.Where(d => d.Edge is not null))
        {
            var key = (d.Level, d.Edge![0], d.Edge[1]);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(d);
        }
        foreach (var e in externalRefs.Where(e => e.External is not null))
        {
            var key = (Identifiers.Root, e.TopLevel, e.External!);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(new DependencyFact(e.From, e.Assembly, "references", e.Symbol, e.File, e.Line, Identifiers.Root, [e.TopLevel, e.External!]));
        }

        CheckPermission(map, groups, violations);
        CheckRealization(map, groups, violations);
        CheckPolicy(map, groups, violations);
        CheckExternals(map, externalRefs, violations);

        var leavesOfFile = LeavesOfFiles(analysis, leafOf);
        return new Facts
        {
            Commit = options.Commit,
            Map = map,
            Types = analysis.Types.Select(t => new TypeFact(t.Name, t.Assembly, leafOf.GetValueOrDefault(t.Name), boundBy.GetValueOrDefault(t.Name), t.Files, t.Generator)).ToList(),
            Files = analysis.Files.Select(f => new FileFact(f.Path, f.Hash, leavesOfFile.GetValueOrDefault(f.Path, []), f.Generator)).ToList(),
            Dependencies = dependencies,
            ExternalRefs = Aggregate(externalRefs),
            Unresolvable = analysis.Unresolvable.Select(u => new UnresolvableFact(u.FromType, u.Symbol, u.Reason, u.File, u.Line)).ToList(),
            Metrics = ComputeMetrics(map, analysis, dependencies, externalRefs, groups, leafOf, boundBy),
            Violations = violations.OrderBy(v => RuleOrder(v.Rule)).ThenBy(v => v.Message, StringComparer.Ordinal).ToList(),
            Warnings = warnings,
        };
    }

    private static int RuleOrder(string rule) => rule switch
    {
        "well-formedness" => 0, "partition" => 1, "permission" => 2, "realization" => 3, "policy" => 4, "externals" => 5, _ => 6,
    };

    // ---------- Rule 2: well-formedness ----------

    /// <summary>Checks the map's own shape and returns each leaf's parsed patterns.</summary>
    private static Dictionary<string, List<TypePattern>> CheckWellFormedness(ArchMap map, CheckOptions options, List<ViolationFact> violations, List<string> warnings)
    {
        void Fail(string message) => violations.Add(new ViolationFact("well-formedness", message, null, null));

        if (options.MapText is not null && options.MapText != MapWriter.Write(map))
            Fail("the map file is not in canonical form; run `archmap fmt`");

        var leaves = new Dictionary<string, List<TypePattern>>(StringComparer.Ordinal);
        foreach (var (id, node) in map.Nodes)
        {
            if (!Identifiers.IsValid(id)) Fail($"\"{id}\" is not a valid identifier");
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
        }

        var seen = new HashSet<(string, string)>();
        foreach (var e in map.Edges)
        {
            if (!map.Exists(e.From) || e.From == Identifiers.Root) { Fail($"edge {e.From} → {e.To}: \"{e.From}\" does not exist"); continue; }
            if (!map.Exists(e.To) || e.To == Identifiers.Root) { Fail($"edge {e.From} → {e.To}: \"{e.To}\" does not exist"); continue; }
            if (e.From == e.To) Fail($"edge {e.From} → {e.To}: an edge cannot join a node to itself");
            if (map.Externals.ContainsKey(e.From)) Fail($"edge {e.From} → {e.To}: an external cannot depend on anything");
            if (Identifiers.ParentOrRoot(e.From) != Identifiers.ParentOrRoot(e.To))
                Fail($"edge {e.From} → {e.To}: both ends must be siblings");
            if (!seen.Add((e.From, e.To))) Fail($"edge {e.From} → {e.To} is declared twice");
        }

        foreach (var f in map.Policies.Forbid)
        {
            foreach (var pattern in new[] { f.From, f.To })
            {
                var id = pattern.EndsWith(".*", StringComparison.Ordinal) ? pattern[..^2] : pattern;
                if (id != "*" && !map.Exists(id)) Fail($"policies.forbid: \"{pattern}\" names nothing in the map");
            }
        }
        foreach (var a in map.Policies.Acyclic)
        {
            if (!map.IsContainer(a)) Fail($"policies.acyclic: \"{a}\" is not a container");
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

    // ---------- Rule 1: partition ----------

    /// <summary>Binds every type to a leaf by attribute, then by most specific pattern, and reports the types that bind to none, to several, or by a bad attribute.</summary>
    private static (Dictionary<string, string> LeafOf, Dictionary<string, string> BoundBy) Partition(ArchMap map, Dictionary<string, List<TypePattern>> leaves, AnalysisResult analysis, List<ViolationFact> violations)
    {
        var leafOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var boundBy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in analysis.Types)
        {
            if (type.MisattributedNested is not null)
            {
                violations.Add(new ViolationFact("partition",
                    $"{type.Name} is misattributed: nested type {type.MisattributedNested} carries [Archmap], but nested types follow their container",
                    new { type = type.Name, nested = type.MisattributedNested }, null));
                continue;
            }
            if (type.Attribute is not null)
            {
                if (leaves.ContainsKey(type.Attribute))
                {
                    leafOf[type.Name] = type.Attribute;
                    boundBy[type.Name] = "attribute";
                }
                else
                {
                    violations.Add(new ViolationFact("partition",
                        $"{type.Name} is misattributed: [Archmap(\"{type.Attribute}\")] names no leaf of the map",
                        new { type = type.Name, attribute = type.Attribute }, null));
                }
                continue;
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
            if (hits.Count == 1)
            {
                leafOf[type.Name] = hits[0].Leaf;
                boundBy[type.Name] = "pattern";
            }
            else if (hits.Count == 0)
            {
                violations.Add(new ViolationFact("partition", $"{type.Name} is unmapped: no leaf binds it",
                    new { type = type.Name, assembly = type.Assembly }, ProposeLeaf(map, leaves, type)));
            }
            else
            {
                var competing = string.Join(", ", hits.Select(h => $"{h.Leaf} ({h.Pattern.Text})"));
                violations.Add(new ViolationFact("partition", $"{type.Name} is ambiguous: bound equally by {competing}",
                    new { type = type.Name, leaves = hits.Select(h => h.Leaf).ToList() }, null));
            }
        }
        return (leafOf, boundBy);
    }

    /// <summary>
    /// Proposes a leaf for an unmapped type: a leaf bound to the type's namespace, placed
    /// beside the existing leaf whose patterns share the longest namespace prefix with it.
    /// A prefix that every leaf shares, such as a product root namespace, does not count,
    /// so a type that resembles no leaf is proposed at the top level.
    /// </summary>
    private static ProposalFact ProposeLeaf(ArchMap map, Dictionary<string, List<TypePattern>> leaves, TypeInfo type)
    {
        var ns = type.Namespace;
        var literals = leaves.SelectMany(kv => kv.Value.Select(p => (Id: kv.Key, Literal: p.LiteralPrefix))).Where(l => l.Literal.Length > 0).ToList();
        var common = literals.Count == 0 ? 0 : literals.Min(l => SharedSegments(l.Literal, literals[0].Literal));
        string? closest = null;
        var best = common;
        foreach (var (leafId, literal) in literals)
        {
            var shared = SharedSegments(literal, ns);
            if (shared > best) { best = shared; closest = leafId; }
        }
        var parent = closest is null ? Identifiers.Root : Identifiers.ParentOrRoot(closest);
        var segment = ns.Length == 0 ? "global" : ns[(ns.LastIndexOf('.') + 1)..];
        var baseId = parent == Identifiers.Root ? Identifiers.NormalizeSegment(segment) : parent + "." + Identifiers.NormalizeSegment(segment);
        var id = baseId;
        for (var n = 2; map.Exists(id); n++) id = baseId + "-" + n;
        var bind = ns.Length == 0 ? type.Assembly + "::*" : ns + ".**";
        return new ProposalFact("add-leaf", Node: id, Name: segment, Bind: [bind]);
    }

    private static int SharedSegments(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var sa = a.Split('.');
        var sb = b.Split('.');
        var n = 0;
        while (n < sa.Length && n < sb.Length && sa[n] == sb[n]) n++;
        return n;
    }

    /// <summary>Maps each file to the sorted leaves of the types it declares.</summary>
    private static Dictionary<string, IReadOnlyList<string>> LeavesOfFiles(AnalysisResult analysis, Dictionary<string, string> leafOf)
    {
        var sets = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var type in analysis.Types)
        {
            if (!leafOf.TryGetValue(type.Name, out var leaf)) continue;
            foreach (var file in type.Files)
            {
                if (!sets.TryGetValue(file, out var set)) sets[file] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(leaf);
            }
        }
        return sets.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.Ordinal);
    }

    // ---------- Lifting ----------

    private static List<DependencyFact> Lift(AnalysisResult analysis, Dictionary<string, string> leafOf)
    {
        var result = new List<DependencyFact>();
        foreach (var d in analysis.Dependencies)
        {
            if (!leafOf.TryGetValue(d.FromType, out var from) || !leafOf.TryGetValue(d.ToType, out var to)) continue;
            if (from == to)
            {
                result.Add(new DependencyFact(d.FromType, d.ToType, d.Kind, d.Symbol, d.File, d.Line, from, null));
                continue;
            }
            var (level, lf, lt) = Identifiers.Lift(from, to);
            result.Add(new DependencyFact(d.FromType, d.ToType, d.Kind, d.Symbol, d.File, d.Line, level, [lf, lt]));
        }
        return result;
    }

    /// <summary>
    /// Resolves each external reference to the external whose pattern matches its
    /// assembly. When several patterns match, the one with the longest literal part
    /// wins, so that <c>System.Text.Json</c> beats <c>System.*</c>.
    /// </summary>
    private static List<ExternalRefFact> ResolveExternals(ArchMap map, AnalysisResult analysis, Dictionary<string, string> leafOf)
    {
        var patterns = map.Externals
            .SelectMany(kv => kv.Value.Bind.Select(p => (Id: kv.Key, Regex: AssemblyPattern(p), Specificity: p.Replace("*", "").Length)))
            .OrderByDescending(p => p.Specificity).ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var result = new List<ExternalRefFact>();
        foreach (var e in analysis.ExternalReferences)
        {
            if (!leafOf.TryGetValue(e.FromType, out var leaf)) continue;
            if (!cache.TryGetValue(e.Assembly, out var external))
            {
                external = patterns.FirstOrDefault(p => p.Regex.IsMatch(e.Assembly)).Id;
                cache[e.Assembly] = external;
            }
            result.Add(new ExternalRefFact(e.FromType, e.Assembly, e.Symbol, e.File, e.Line, 1, external, Identifiers.TopLevelOf(leaf)));
        }
        return result;
    }

    private static Regex AssemblyPattern(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);

    /// <summary>Collapses per-reference external facts into one entry per type and assembly, keeping the first symbol and location and the total count.</summary>
    private static List<ExternalRefFact> Aggregate(List<ExternalRefFact> refs) =>
        refs.GroupBy(e => (e.From, e.Assembly))
            .Select(g => g.First() with { Count = g.Sum(e => e.Count) })
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.Assembly, StringComparer.Ordinal)
            .ToList();

    // ---------- Rule 3: permission ----------

    private static void CheckPermission(ArchMap map, Dictionary<(string Level, string From, string To), List<DependencyFact>> groups, List<ViolationFact> violations)
    {
        foreach (var ((level, from, to), evidence) in groups.OrderBy(g => g.Key.Level, StringComparer.Ordinal).ThenBy(g => g.Key.From, StringComparer.Ordinal).ThenBy(g => g.Key.To, StringComparer.Ordinal))
        {
            if (map.PolicyOf(level) != ArchMap.Declared) continue;
            if (map.FindEdge(from, to) is not null) continue;
            var first = evidence[0];
            violations.Add(new ViolationFact("permission",
                $"{from} → {to} is not declared at level {level}; first seen at {first.File}:{first.Line} ({first.Kind} {first.Symbol}); {evidence.Count} reference(s)",
                new { level, from, to, count = evidence.Count, examples = evidence.Take(5).ToList() },
                new ProposalFact("add-edge", From: from, To: to)));
        }
    }

    // ---------- Rule 4: realization ----------

    private static void CheckRealization(ArchMap map, Dictionary<(string Level, string From, string To), List<DependencyFact>> groups, List<ViolationFact> violations)
    {
        foreach (var e in map.Edges)
        {
            var level = Identifiers.ParentOrRoot(e.From);
            if (map.PolicyOf(level) != ArchMap.Declared || e.Asserted) continue;
            groups.TryGetValue((level, e.From, e.To), out var evidence);
            if (evidence is null || evidence.Count == 0)
            {
                violations.Add(new ViolationFact("realization",
                    $"{e.From} → {e.To} is declared at level {level} but no reference realizes it",
                    new { level, from = e.From, to = e.To },
                    new ProposalFact("remove-edge", From: e.From, To: e.To)));
                continue;
            }
            if (e.Payload is not null && !evidence.Any(d => CarriesType(d, e.Payload)))
            {
                violations.Add(new ViolationFact("realization",
                    $"{e.From} → {e.To} declares payload {e.Payload} but no reference across it involves that type",
                    new { level, from = e.From, to = e.To, payload = e.Payload, examples = evidence.Take(5).ToList() },
                    null));
            }
        }
    }

    private static bool CarriesType(DependencyFact d, string payload) =>
        d.To == payload || d.To.EndsWith("." + payload, StringComparison.Ordinal)
        || d.Symbol == payload || d.Symbol.EndsWith("." + payload, StringComparison.Ordinal) || d.Symbol.Contains("." + payload + ".", StringComparison.Ordinal);

    // ---------- Rule 5: policy ----------

    private static void CheckPolicy(ArchMap map, Dictionary<(string Level, string From, string To), List<DependencyFact>> groups, List<ViolationFact> violations)
    {
        foreach (var f in map.Policies.Forbid)
        {
            foreach (var ((level, from, to), evidence) in groups)
            {
                if (!Identifiers.PatternMatches(f.From, from) || !Identifiers.PatternMatches(f.To, to)) continue;
                var first = evidence[0];
                violations.Add(new ViolationFact("policy",
                    $"{from} → {to} is forbidden by policy {f.From} → {f.To}; first seen at {first.File}:{first.Line} ({first.Symbol})",
                    new { level, from, to, forbid = f, count = evidence.Count, examples = evidence.Take(5).ToList() }, null));
            }
        }
        foreach (var level in map.Policies.Acyclic)
        {
            foreach (var cycle in CyclesAmong(map, level, groups))
            {
                violations.Add(new ViolationFact("policy",
                    $"children of {level} form a cycle: {string.Join(" → ", cycle)} → {cycle[0]}",
                    new { level, cycle }, null));
            }
        }
    }

    private static void CheckExternals(ArchMap map, List<ExternalRefFact> externalRefs, List<ViolationFact> violations)
    {
        if (map.Policies.Root.Externals != ArchMap.Declared) return;
        foreach (var group in externalRefs.Where(e => e.External is null).GroupBy(e => e.Assembly).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            var count = group.Sum(e => e.Count);
            violations.Add(new ViolationFact("externals",
                $"assembly {group.Key} is referenced but no external binds it; first seen at {first.File}:{first.Line} ({first.Symbol}); {count} reference(s)",
                new { assembly = group.Key, count, examples = group.Take(5).ToList() }, null));
        }
    }

    /// <summary>Returns the strongly connected components with more than one member among a level's children, each listed in a cycle order.</summary>
    private static List<List<string>> CyclesAmong(ArchMap map, string level, Dictionary<(string Level, string From, string To), List<DependencyFact>> groups)
    {
        var children = map.ChildrenOf(level).ToList();
        var adjacency = children.ToDictionary(c => c, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (l, from, to) in groups.Keys)
        {
            if (l == level && adjacency.ContainsKey(from) && adjacency.ContainsKey(to)) adjacency[from].Add(to);
        }
        return Tarjan(children, adjacency);
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

    // ---------- Metrics ----------

    private static SortedDictionary<string, NodeMetrics> ComputeMetrics(ArchMap map, AnalysisResult analysis, List<DependencyFact> dependencies, List<ExternalRefFact> externalRefs,
        Dictionary<(string Level, string From, string To), List<DependencyFact>> groups, Dictionary<string, string> leafOf, Dictionary<string, string> boundBy)
    {
        var metrics = new SortedDictionary<string, NodeMetrics>(StringComparer.Ordinal);
        foreach (var level in new[] { Identifiers.Root }.Concat(map.Nodes.Where(kv => !kv.Value.IsLeaf).Select(kv => kv.Key)))
        {
            var internalCount = 0;
            var crossing = 0;
            foreach (var d in dependencies)
            {
                var a = Identifiers.IsUnder(leafOf[d.From], level);
                var b = Identifiers.IsUnder(leafOf[d.To], level);
                if (a && b) internalCount++;
                else if (a || b) crossing++;
            }
            crossing += externalRefs.Where(e => Identifiers.IsUnder(leafOf[e.From], level)).Sum(e => e.Count);
            var attributed = boundBy.Count(kv => kv.Value == "attribute" && Identifiers.IsUnder(leafOf[kv.Key], level));
            metrics[level] = new NodeMetrics(internalCount, crossing, CyclesAmong(map, level, groups), attributed);
        }
        return metrics;
    }
}
