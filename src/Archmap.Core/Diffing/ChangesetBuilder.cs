using Archmap.Checking;
using Archmap.Model;

namespace Archmap.Diffing;

/// <summary>Combines a base and a head facts document into a changeset document.</summary>
public static class ChangesetBuilder
{
    public static Changeset Build(Facts baseFacts, Facts headFacts, List<FileHunks>? hunks = null)
    {
        var files = DiffFiles(baseFacts, headFacts);
        return new Changeset
        {
            Base = baseFacts.Commit,
            Head = headFacts.Commit,
            Map = headFacts.Map,
            Structural = DiffStructure(baseFacts, headFacts),
            Content = Rollup(baseFacts.Map, headFacts.Map, files, (hunks ?? []).Where(h => h.Trivial == true).Select(h => h.Path).ToHashSet(StringComparer.Ordinal)),
            Files = files,
            Types = DiffTypes(baseFacts, headFacts),
            Edges = DiffEdges(baseFacts, headFacts),
            Metrics = DiffMetrics(baseFacts, headFacts),
            Levels = headFacts.Levels,
            Views = headFacts.Views,
            Hunks = hunks ?? [],
        };
    }

    private static List<TypeChange> DiffTypes(Facts baseFacts, Facts headFacts)
    {
        var baseTypes = baseFacts.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var headTypes = headFacts.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var result = new List<TypeChange>();
        foreach (var name in baseTypes.Keys.Union(headTypes.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            baseTypes.TryGetValue(name, out var b);
            headTypes.TryGetValue(name, out var h);
            var status = b is null ? "added" : h is null ? "removed" : b.Leaf != h.Leaf ? "moved" : "unchanged";
            result.Add(new TypeChange(name, b?.Leaf, h?.Leaf, (h ?? b)!.Files, status));
        }
        return result;
    }

    private static SortedDictionary<string, MetricsChange> DiffMetrics(Facts baseFacts, Facts headFacts)
    {
        var result = new SortedDictionary<string, MetricsChange>(StringComparer.Ordinal);
        foreach (var id in baseFacts.Nodes.Keys.Union(headFacts.Nodes.Keys))
            result[id] = new MetricsChange(baseFacts.Nodes.GetValueOrDefault(id), headFacts.Nodes.GetValueOrDefault(id));
        return result;
    }

    private static StructuralDiff DiffStructure(Facts baseFacts, Facts headFacts)
    {
        var bm = baseFacts.Map;
        var hm = headFacts.Map;
        var nodes = new List<NodeChange>();
        var views = new List<ViewChange>();

        foreach (var id in hm.Nodes.Keys.Union(bm.Nodes.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var inBase = bm.Nodes.TryGetValue(id, out var b);
            var inHead = hm.Nodes.TryGetValue(id, out var h);
            if (inHead && !inBase) nodes.Add(new NodeChange(id, "added", "node", h!.IsLeaf ? $"binds {headFacts.Types.Count(t => t.Leaf == id)} type(s)" : "container"));
            else if (inBase && !inHead) nodes.Add(new NodeChange(id, "removed", "node", b!.IsLeaf ? $"bound {baseFacts.Types.Count(t => t.Leaf == id)} type(s)" : "container"));
            else
            {
                var details = new List<string>();
                if (b!.Name != h!.Name) details.Add($"name {b.Name} → {h.Name}");
                if (b.Purpose != h.Purpose) details.Add("purpose changed");
                if (b.Note != h.Note) details.Add("note changed");
                if (!SeqEq(b.Bind, h.Bind)) details.Add(b.IsLeaf != h.IsLeaf ? (h.IsLeaf ? "became a leaf" : "became a container") : "bindings changed");
                if (details.Count > 0) nodes.Add(new NodeChange(id, "modified", "node", string.Join("; ", details)));
            }
        }
        foreach (var id in hm.Externals.Keys.Union(bm.Externals.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var inBase = bm.Externals.TryGetValue(id, out var b);
            var inHead = hm.Externals.TryGetValue(id, out var h);
            if (inHead && !inBase) nodes.Add(new NodeChange(id, "added", "external", null));
            else if (inBase && !inHead) nodes.Add(new NodeChange(id, "removed", "external", null));
            else if (b!.Name != h!.Name || b.Purpose != h.Purpose || !SeqEq(b.Bind, h.Bind)) nodes.Add(new NodeChange(id, "modified", "external", null));
        }
        foreach (var id in hm.Views.Keys.Union(bm.Views.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var inBase = bm.Views.TryGetValue(id, out var b);
            var inHead = hm.Views.TryGetValue(id, out var h);
            if (inHead && !inBase) views.Add(new ViewChange(id, "added", $"at {h!.Level}"));
            else if (inBase && !inHead) views.Add(new ViewChange(id, "removed", null));
            else
            {
                var details = new List<string>();
                if (b!.Name != h!.Name) details.Add("name changed");
                if (b.Level != h.Level) details.Add($"level {b.Level} → {h.Level}");
                var changedGroups = h.Groups.Keys.Union(b.Groups.Keys)
                    .Where(g => !b.Groups.TryGetValue(g, out var bg) || !h.Groups.TryGetValue(g, out var hg) || bg.Name != hg.Name || bg.Purpose != hg.Purpose || !SeqEq(bg.Leaves, hg.Leaves))
                    .OrderBy(g => g, StringComparer.Ordinal).ToList();
                if (changedGroups.Count > 0) details.Add("groups changed: " + string.Join(", ", changedGroups));
                if (details.Count > 0) views.Add(new ViewChange(id, "modified", string.Join("; ", details)));
            }
        }

        var baseTypes = baseFacts.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var types = headFacts.Types
            .Where(t => baseTypes.TryGetValue(t.Name, out var bt) && bt.Leaf != t.Leaf)
            .Select(t => new TypeMove(t.Name, baseTypes[t.Name].Leaf, t.Leaf, t.By))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        return new StructuralDiff { Nodes = nodes, Views = views, Types = types };
    }

    private static bool SeqEq(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.SequenceEqual(b, StringComparer.Ordinal));

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
            result.Add(new FileChange(path, status, b?.Leaves ?? [], h?.Leaves ?? []));
        }
        return result;
    }

    /// <summary>
    /// Rolls file changes up through the hierarchy. A file counts under every node that contains any leaf its types bind
    /// to. The trivial paths are the modified files whose edits are all trivial.
    /// </summary>
    private static SortedDictionary<string, NodeContent> Rollup(ArchMap baseMap, ArchMap headMap, List<FileChange> files, HashSet<string> trivial)
    {
        static bool Under(IReadOnlyList<string> leaves, string id) => leaves.Any(l => Identifiers.IsUnder(l, id));
        var content = new SortedDictionary<string, NodeContent>(StringComparer.Ordinal);
        var ids = new[] { Identifiers.Root }.Concat(headMap.Nodes.Keys.Union(baseMap.Nodes.Keys)).ToList();
        if (files.Any(f => f.LeavesBase.Contains(ArchMap.Unsorted) || f.LeavesHead.Contains(ArchMap.Unsorted))) ids.Add(ArchMap.Unsorted);
        foreach (var id in ids.Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            var total = files.Count(f => Under(f.LeavesHead, id));
            var added = files.Count(f => f.Status == "added" && Under(f.LeavesHead, id));
            var removed = files.Count(f => f.Status == "removed" && Under(f.LeavesBase, id));
            var modified = files.Count(f => f.Status == "modified" && Under(f.LeavesHead, id));
            var trivialCount = files.Count(f => f.Status == "modified" && trivial.Contains(f.Path) && Under(f.LeavesHead, id));
            string status;
            if (id != Identifiers.Root && id != ArchMap.Unsorted && !baseMap.Nodes.ContainsKey(id)) status = "added";
            else if (id != Identifiers.Root && id != ArchMap.Unsorted && !headMap.Nodes.ContainsKey(id)) status = "removed";
            else if (added + removed + modified == 0) status = "unchanged";
            else status = headMap.IsContainer(id) ? "children" : "modified";
            content[id] = new NodeContent(total, added, removed, modified, status, trivialCount);
        }
        return content;
    }

    private static List<EdgeChange> DiffEdges(Facts baseFacts, Facts headFacts)
    {
        var baseEdges = baseFacts.LeafEdges.ToDictionary(e => (e.From, e.To), e => e.Count);
        var headEdges = headFacts.LeafEdges.ToDictionary(e => (e.From, e.To), e => e.Count);
        var result = new List<EdgeChange>();
        foreach (var key in baseEdges.Keys.Union(headEdges.Keys).OrderBy(k => k.From, StringComparer.Ordinal).ThenBy(k => k.To, StringComparer.Ordinal))
        {
            var b = baseEdges.GetValueOrDefault(key);
            var h = headEdges.GetValueOrDefault(key);
            var status = b == 0 ? "added" : h == 0 ? "removed" : b != h ? "modified" : "unchanged";
            var evidence = status == "unchanged" ? null : Evidence(baseFacts, headFacts, key.From, key.To);
            result.Add(new EdgeChange(key.From, key.To, b, h, status, evidence));
        }
        return result;
    }

    /// <summary>Returns the type pairs behind an edge whose counts differ between base and head, largest change first.</summary>
    private static List<PairChange> Evidence(Facts baseFacts, Facts headFacts, string from, string to)
    {
        var pairs = new Dictionary<(string, string), (int Base, int Head)>();
        foreach (var (f, t, n) in Explain.Pairs(baseFacts, from, to)) pairs[(f, t)] = (n, 0);
        foreach (var (f, t, n) in Explain.Pairs(headFacts, from, to)) pairs[(f, t)] = (pairs.GetValueOrDefault((f, t)).Base, n);
        return pairs.Where(p => p.Value.Base != p.Value.Head)
            .Select(p => new PairChange(p.Key.Item1, p.Key.Item2, p.Value.Base, p.Value.Head))
            .OrderByDescending(p => Math.Abs(p.Head - p.Base)).ThenBy(p => p.From, StringComparer.Ordinal).ThenBy(p => p.To, StringComparer.Ordinal)
            .ToList();
    }
}
