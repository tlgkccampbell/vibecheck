using Archmap.Checking;
using Archmap.Model;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Archmap.Diffing;

/// <summary>Combines a base and a head facts document into a changeset document, including the minimality findings of section 4.2.</summary>
public static class ChangesetBuilder
{
    public static Changeset Build(Facts baseFacts, Facts headFacts, List<FileHunks>? hunks = null)
    {
        var baseGroups = Groups(baseFacts);
        var headGroups = Groups(headFacts);
        var files = DiffFiles(baseFacts, headFacts);
        return new Changeset
        {
            Base = baseFacts.Commit,
            Head = headFacts.Commit,
            Structural = DiffStructure(baseFacts, headFacts, baseGroups, headGroups),
            Content = Rollup(baseFacts.Map, headFacts.Map, files),
            Files = files,
            Edges = DiffEdges(headFacts.Map, baseGroups, headGroups),
            Hunks = hunks ?? [],
        };
    }

    /// <summary>Groups a facts document's lifted dependencies and bound external references by (level, from, to).</summary>
    private static Dictionary<(string Level, string From, string To), List<DependencyFact>> Groups(Facts facts)
    {
        var groups = new Dictionary<(string, string, string), List<DependencyFact>>();
        void Add((string, string, string) key, DependencyFact d)
        {
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(d);
        }
        foreach (var d in facts.Dependencies.Where(d => d.Edge is not null)) Add((d.Level, d.Edge![0], d.Edge[1]), d);
        foreach (var e in facts.ExternalRefs.Where(e => e.External is not null))
            Add((Identifiers.Root, e.TopLevel, e.External!), new DependencyFact(e.From, e.Assembly, "references", e.Symbol, Identifiers.LastSegmentOf(e.Symbol), e.Line, Identifiers.Root, [e.TopLevel, e.External!]));
        return groups;
    }

    private static List<string> Evidence(List<DependencyFact>? deps) =>
        (deps ?? []).Take(5).Select(d => $"{d.From}:{d.Line} {d.Kind} {d.Symbol}").ToList();

    private static StructuralDiff DiffStructure(Facts baseFacts, Facts headFacts,
        Dictionary<(string Level, string From, string To), List<DependencyFact>> baseGroups,
        Dictionary<(string Level, string From, string To), List<DependencyFact>> headGroups)
    {
        var bm = baseFacts.Map;
        var hm = headFacts.Map;
        var nodes = new List<NodeStructuralChange>();
        var edges = new List<EdgeStructuralChange>();
        var policies = new List<PolicyChange>();

        foreach (var id in hm.Nodes.Keys.Union(bm.Nodes.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var inBase = bm.Nodes.TryGetValue(id, out var b);
            var inHead = hm.Nodes.TryGetValue(id, out var h);
            if (inHead && !inBase)
            {
                var bound = headFacts.Files.Count(f => f!.Leaf == id);
                var isLeaf = h!.IsLeaf;
                nodes.Add(new NodeStructuralChange(id, "added", "node", null,
                    isLeaf ? [$"binds {bound} file(s) at head"] : ["container"], isLeaf && bound == 0));
            }
            else if (inBase && !inHead)
            {
                var stillBound = b!.IsLeaf ? MatchCount(b.Bind!, headFacts.Files.Select(f => f.Path)) : 0;
                nodes.Add(new NodeStructuralChange(id, "removed", "node", null,
                    b.IsLeaf ? [$"its bindings match {stillBound} file(s) at head"] : ["container"], stillBound > 0));
            }
            else if (inBase && inHead)
            {
                var details = new List<string>();
                if (b!.Name != h!.Name) details.Add($"name {b.Name} → {h.Name}");
                if (b.Purpose != h.Purpose) details.Add("purpose changed");
                if (b.Note != h.Note) details.Add("note changed");
                if (!SeqEq(b.Bind, h.Bind)) details.Add("bindings changed");
                if ((b.Edges ?? ArchMap.Observed) != (h.Edges ?? ArchMap.Observed))
                    policies.Add(new PolicyChange(id, b.Edges ?? ArchMap.Observed, h.Edges ?? ArchMap.Observed));
                if (details.Count > 0) nodes.Add(new NodeStructuralChange(id, "modified", "node", string.Join("; ", details), [], false));
            }
        }
        foreach (var id in hm.Externals.Keys.Union(bm.Externals.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var inBase = bm.Externals.ContainsKey(id);
            var inHead = hm.Externals.ContainsKey(id);
            if (inHead && !inBase)
            {
                var refs = headFacts.ExternalRefs.Count(e => e.External == id);
                nodes.Add(new NodeStructuralChange(id, "added", "external", null, [$"{refs} reference(s) at head"], refs == 0));
            }
            else if (inBase && !inHead)
            {
                nodes.Add(new NodeStructuralChange(id, "removed", "external", null, [], false));
            }
        }
        if (bm.Policies.Root.Edges != hm.Policies.Root.Edges) policies.Add(new PolicyChange(Identifiers.Root, bm.Policies.Root.Edges, hm.Policies.Root.Edges));

        var baseEdges = bm.Edges.ToDictionary(e => (e.From, e.To));
        var headEdges = hm.Edges.ToDictionary(e => (e.From, e.To));
        foreach (var key in headEdges.Keys.Union(baseEdges.Keys).OrderBy(k => k.From, StringComparer.Ordinal).ThenBy(k => k.To, StringComparer.Ordinal))
        {
            var level = Identifiers.ParentOrRoot(key.From);
            baseGroups.TryGetValue((level, key.From, key.To), out var bd);
            headGroups.TryGetValue((level, key.From, key.To), out var hd);
            var baseRefs = bd?.Count ?? 0;
            var headRefs = hd?.Count ?? 0;
            var inBase = baseEdges.TryGetValue(key, out var be);
            var inHead = headEdges.TryGetValue(key, out var he);
            if (inHead && !inBase)
            {
                edges.Add(new EdgeStructuralChange(level, key.From, key.To, "added", baseRefs, headRefs, null, Evidence(hd),
                    Gratuitous: !he!.Asserted && (headRefs == 0 || baseRefs > 0)));
            }
            else if (inBase && !inHead)
            {
                edges.Add(new EdgeStructuralChange(level, key.From, key.To, "removed", baseRefs, headRefs, null, Evidence(bd),
                    Gratuitous: !be!.Asserted && (baseRefs == 0 || headRefs > 0)));
            }
            else if (inBase && inHead)
            {
                var details = new List<string>();
                if (be!.Label != he!.Label) details.Add($"label {be.Label ?? "none"} → {he.Label ?? "none"}");
                if (be.Payload != he.Payload) details.Add($"payload {be.Payload ?? "none"} → {he.Payload ?? "none"}");
                if (be.Asserted != he.Asserted) details.Add($"asserted {be.Asserted} → {he.Asserted}");
                if (details.Count > 0) edges.Add(new EdgeStructuralChange(level, key.From, key.To, "modified", baseRefs, headRefs, string.Join("; ", details), [], false));
            }
        }
        return new StructuralDiff { Nodes = nodes, Edges = edges, Policies = policies };
    }

    private static bool SeqEq(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.SequenceEqual(b, StringComparer.Ordinal));

    private static int MatchCount(IReadOnlyList<string> patterns, IEnumerable<string> paths)
    {
        var m = new Matcher(StringComparison.Ordinal);
        foreach (var p in patterns) m.AddInclude(p);
        return paths.Count(p => m.Match(p).HasMatches);
    }

    private static List<FileChange> DiffFiles(Facts baseFacts, Facts headFacts)
    {
        var baseFiles = baseFacts.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var headFiles = headFacts.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var result = new List<FileChange>();
        foreach (var path in baseFiles.Keys.Union(headFiles.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            baseFiles.TryGetValue(path, out var b);
            headFiles.TryGetValue(path, out var h);
            var status = b is null ? "added" : h is null ? "removed" : b.Hash != h.Hash ? "modified" : "unchanged";
            result.Add(new FileChange(path, status, b?.Leaf, h?.Leaf));
        }
        return result;
    }

    private static SortedDictionary<string, NodeContent> Rollup(ArchMap baseMap, ArchMap headMap, List<FileChange> files)
    {
        var content = new SortedDictionary<string, NodeContent>(StringComparer.Ordinal);
        var ids = new[] { Identifiers.Root }.Concat(headMap.Nodes.Keys.Union(baseMap.Nodes.Keys)).Distinct().OrderBy(k => k, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var total = files.Count(f => f.LeafHead is not null && Identifiers.IsUnder(f.LeafHead, id));
            var added = files.Count(f => f.Status == "added" && f.LeafHead is not null && Identifiers.IsUnder(f.LeafHead, id));
            var removed = files.Count(f => f.Status == "removed" && f.LeafBase is not null && Identifiers.IsUnder(f.LeafBase, id));
            var modified = files.Count(f => f.Status == "modified" && f.LeafHead is not null && Identifiers.IsUnder(f.LeafHead, id));
            string status;
            if (id != Identifiers.Root && !baseMap.Nodes.ContainsKey(id)) status = "added";
            else if (id != Identifiers.Root && !headMap.Nodes.ContainsKey(id)) status = "removed";
            else if (added + removed + modified == 0) status = "unchanged";
            else status = headMap.IsContainer(id) ? "children" : "modified";
            content[id] = new NodeContent(total, added, removed, modified, status);
        }
        return content;
    }

    private static List<EdgeChange> DiffEdges(ArchMap headMap,
        Dictionary<(string Level, string From, string To), List<DependencyFact>> baseGroups,
        Dictionary<(string Level, string From, string To), List<DependencyFact>> headGroups)
    {
        var result = new List<EdgeChange>();
        foreach (var key in baseGroups.Keys.Union(headGroups.Keys).OrderBy(k => k.Level, StringComparer.Ordinal).ThenBy(k => k.From, StringComparer.Ordinal).ThenBy(k => k.To, StringComparer.Ordinal))
        {
            var b = baseGroups.TryGetValue(key, out var bd) ? bd.Count : 0;
            var h = headGroups.TryGetValue(key, out var hd) ? hd.Count : 0;
            var status = b == 0 ? "added" : h == 0 ? "removed" : b != h ? "modified" : "unchanged";
            var observed = headMap.IsContainer(key.Level) && headMap.PolicyOf(key.Level) == ArchMap.Observed;
            var undeclared = status == "added" && observed && headMap.FindEdge(key.From, key.To) is null;
            result.Add(new EdgeChange(key.Level, key.From, key.To, b, h, status, undeclared));
        }
        return result;
    }
}
