using System.Text.RegularExpressions;
using Archmap.Analysis;
using Archmap.Format;
using Archmap.Model;
using Microsoft.Extensions.FileSystemGlobbing;

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

        CheckWellFormedness(map, options, violations, warnings);
        if (violations.Count > 0)
        {
            return new Facts
            {
                Commit = options.Commit,
                Map = map,
                Files = analysis.Files.Select(f => new FileFact(f.Path, null, f.Hash)).ToList(),
                Dependencies = [],
                ExternalRefs = [],
                Unresolvable = analysis.Unresolvable.Select(u => new UnresolvableFact(u.FromFile, u.Symbol, u.Generator, u.Line)).ToList(),
                Metrics = new SortedDictionary<string, NodeMetrics>(StringComparer.Ordinal),
                Violations = violations,
                Warnings = warnings,
            };
        }

        var leafOf = Partition(map, analysis, violations);
        CheckCohesion(map, analysis, leafOf, violations);

        var listed = analysis.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var unlisted = analysis.Dependencies.Count(d => !listed.Contains(d.FromFile) || !listed.Contains(d.ToFile));
        if (unlisted > 0) warnings.Add($"analysis bug: {unlisted} dependencies reference files the analysis did not list and were ignored");

        var dependencies = Lift(map, analysis, leafOf);
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
            list.Add(new DependencyFact(e.From, e.Assembly, "references", e.Symbol, Identifiers.LastSegmentOf(e.Symbol), e.Line, Identifiers.Root, [e.TopLevel, e.External!]));
        }

        CheckPermission(map, groups, violations);
        CheckRealization(map, groups, violations);
        CheckPolicy(map, groups, violations);
        CheckExternals(map, externalRefs, violations);

        return new Facts
        {
            Commit = options.Commit,
            Map = map,
            Files = analysis.Files.Select(f => new FileFact(f.Path, leafOf.GetValueOrDefault(f.Path), f.Hash)).ToList(),
            Dependencies = dependencies,
            ExternalRefs = Aggregate(externalRefs),
            Unresolvable = analysis.Unresolvable.Select(u => new UnresolvableFact(u.FromFile, u.Symbol, u.Generator, u.Line)).ToList(),
            Metrics = ComputeMetrics(map, dependencies, externalRefs, groups, leafOf),
            Violations = violations.OrderBy(v => RuleOrder(v.Rule)).ThenBy(v => v.Message, StringComparer.Ordinal).ToList(),
            Warnings = warnings,
        };
    }

    private static int RuleOrder(string rule) => rule switch
    {
        "well-formedness" => 0, "partition" => 1, "cohesion" => 2, "permission" => 3, "realization" => 4, "policy" => 5, "externals" => 6, _ => 7,
    };

    // ---------- Rule 3: well-formedness ----------

    private static void CheckWellFormedness(ArchMap map, CheckOptions options, List<ViolationFact> violations, List<string> warnings)
    {
        void Fail(string message) => violations.Add(new ViolationFact("well-formedness", message, null, null));

        if (options.MapText is not null && options.MapText != MapWriter.Write(map))
            Fail("the map file is not in canonical form; run `archmap fmt`");

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
    }

    // ---------- Rule 1: partition ----------

    private static Dictionary<string, string> Partition(ArchMap map, AnalysisResult analysis, List<ViolationFact> violations)
    {
        var matchers = map.Nodes.Where(kv => kv.Value.IsLeaf).Select(kv =>
        {
            var m = new Matcher(StringComparison.Ordinal);
            foreach (var pattern in kv.Value.Bind!) m.AddInclude(pattern);
            return (Id: kv.Key, Matcher: m);
        }).ToList();

        var leafOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in analysis.Files)
        {
            var hits = matchers.Where(m => m.Matcher.Match(file.Path).HasMatches).Select(m => m.Id).ToList();
            if (hits.Count == 1)
            {
                leafOf[file.Path] = hits[0];
            }
            else if (hits.Count == 0)
            {
                violations.Add(new ViolationFact("partition", $"{file.Path} is unmapped: no leaf binds it",
                    new { file = file.Path }, ProposeLeaf(map, file.Path)));
            }
            else
            {
                violations.Add(new ViolationFact("partition", $"{file.Path} is ambiguous: bound by {string.Join(", ", hits)}",
                    new { file = file.Path, leaves = hits }, null));
            }
        }
        return leafOf;
    }

    /// <summary>
    /// Proposes a leaf for an unmapped file: a leaf bound to the file's directory, placed
    /// beside the existing leaf whose bindings share the longest directory prefix with it.
    /// A prefix that every leaf shares, such as a common <c>src</c> directory, does not
    /// count, so a file that resembles no leaf is proposed at the top level.
    /// </summary>
    private static ProposalFact ProposeLeaf(ArchMap map, string file)
    {
        var dir = file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
        var literals = map.Nodes.Where(kv => kv.Value.IsLeaf)
            .SelectMany(kv => kv.Value.Bind!.Select(p => (Id: kv.Key, Literal: p.Split('*')[0].TrimEnd('/'))))
            .ToList();
        var common = literals.Count == 0 ? 0 : literals.Min(l => SharedPrefixSegments(l.Literal, literals[0].Literal));
        string? closest = null;
        var best = common;
        foreach (var (leafId, literal) in literals)
        {
            var shared = SharedPrefixSegments(literal, dir);
            if (shared > best) { best = shared; closest = leafId; }
        }
        var parent = closest is null ? Identifiers.Root : Identifiers.ParentOrRoot(closest);
        var dirName = dir.Length == 0 ? "root-files" : dir[(dir.LastIndexOf('/') + 1)..];
        var baseId = parent == Identifiers.Root ? Identifiers.NormalizeSegment(dirName) : parent + "." + Identifiers.NormalizeSegment(dirName);
        var id = baseId;
        for (var n = 2; map.Exists(id); n++) id = baseId + "-" + n;
        var bind = dir.Length == 0 ? "*.cs" : dir + "/**/*.cs";
        return new ProposalFact("add-leaf", Node: id, Name: dirName, Bind: [bind]);
    }

    private static int SharedPrefixSegments(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var sa = a.Split('/');
        var sb = b.Split('/');
        var n = 0;
        while (n < sa.Length && n < sb.Length && sa[n] == sb[n]) n++;
        return n;
    }

    // ---------- Rule 2: cohesion ----------

    private static void CheckCohesion(ArchMap map, AnalysisResult analysis, Dictionary<string, string> leafOf, List<ViolationFact> violations)
    {
        foreach (var partial in analysis.PartialTypes)
        {
            var leaves = partial.Files.Select(f => leafOf.GetValueOrDefault(f)).Where(l => l is not null).Distinct().ToList();
            if (leaves.Count > 1)
            {
                violations.Add(new ViolationFact("cohesion",
                    $"partial type {partial.Name} is declared in more than one leaf: {string.Join(", ", leaves)}",
                    new { type = partial.Name, files = partial.Files, leaves }, null));
            }
        }
    }

    // ---------- Lifting ----------

    private static List<DependencyFact> Lift(ArchMap map, AnalysisResult analysis, Dictionary<string, string> leafOf)
    {
        var result = new List<DependencyFact>();
        foreach (var d in analysis.Dependencies)
        {
            if (!leafOf.TryGetValue(d.FromFile, out var from) || !leafOf.TryGetValue(d.ToFile, out var to)) continue;
            if (from == to)
            {
                result.Add(new DependencyFact(d.FromFile, d.ToFile, d.Kind, d.Symbol, d.Type, d.Line, from, null));
                continue;
            }
            var (level, lf, lt) = Identifiers.Lift(from, to);
            result.Add(new DependencyFact(d.FromFile, d.ToFile, d.Kind, d.Symbol, d.Type, d.Line, level, [lf, lt]));
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
            if (!leafOf.TryGetValue(e.FromFile, out var leaf)) continue;
            if (!cache.TryGetValue(e.Assembly, out var external))
            {
                external = patterns.FirstOrDefault(p => p.Regex.IsMatch(e.Assembly)).Id;
                cache[e.Assembly] = external;
            }
            result.Add(new ExternalRefFact(e.FromFile, e.Assembly, e.Symbol, e.Line, 1, external, Identifiers.TopLevelOf(leaf)));
        }
        return result;
    }

    /// <summary>Collapses per-reference external facts into one entry per file and assembly, keeping the first symbol and line and the total count.</summary>
    private static List<ExternalRefFact> Aggregate(List<ExternalRefFact> refs) =>
        refs.GroupBy(e => (e.From, e.Assembly))
            .Select(g => g.First() with { Count = g.Sum(e => e.Count) })
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.Assembly, StringComparer.Ordinal)
            .ToList();

    private static Regex AssemblyPattern(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);

    // ---------- Rule 4: permission ----------

    private static void CheckPermission(ArchMap map, Dictionary<(string Level, string From, string To), List<DependencyFact>> groups, List<ViolationFact> violations)
    {
        foreach (var ((level, from, to), evidence) in groups.OrderBy(g => g.Key.Level, StringComparer.Ordinal).ThenBy(g => g.Key.From, StringComparer.Ordinal).ThenBy(g => g.Key.To, StringComparer.Ordinal))
        {
            if (map.PolicyOf(level) != ArchMap.Declared) continue;
            if (map.FindEdge(from, to) is not null) continue;
            var first = evidence[0];
            violations.Add(new ViolationFact("permission",
                $"{from} → {to} is not declared at level {level}; first seen at {first.From}:{first.Line} ({first.Kind} {first.Symbol}); {evidence.Count} reference(s)",
                new { level, from, to, count = evidence.Count, examples = evidence.Take(5).ToList() },
                new ProposalFact("add-edge", From: from, To: to)));
        }
    }

    // ---------- Rule 5: realization ----------

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
        d.Type == payload || d.Symbol == payload || d.Symbol.EndsWith("." + payload, StringComparison.Ordinal) || d.Symbol.Contains("." + payload + ".", StringComparison.Ordinal);

    // ---------- Rule 6: policy ----------

    private static void CheckPolicy(ArchMap map, Dictionary<(string Level, string From, string To), List<DependencyFact>> groups, List<ViolationFact> violations)
    {
        foreach (var f in map.Policies.Forbid)
        {
            foreach (var ((level, from, to), evidence) in groups)
            {
                if (!Identifiers.PatternMatches(f.From, from) || !Identifiers.PatternMatches(f.To, to)) continue;
                var first = evidence[0];
                violations.Add(new ViolationFact("policy",
                    $"{from} → {to} is forbidden by policy {f.From} → {f.To}; first seen at {first.From}:{first.Line} ({first.Symbol})",
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
                $"assembly {group.Key} is referenced but no external binds it; first seen at {first.From}:{first.Line} ({first.Symbol}); {count} reference(s)",
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

    private static SortedDictionary<string, NodeMetrics> ComputeMetrics(ArchMap map, List<DependencyFact> dependencies, List<ExternalRefFact> externalRefs,
        Dictionary<(string Level, string From, string To), List<DependencyFact>> groups, Dictionary<string, string> leafOf)
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
            metrics[level] = new NodeMetrics(internalCount, crossing, CyclesAmong(map, level, groups));
        }
        return metrics;
    }
}
