using Archmap.Diffing;
using Archmap.Model;

namespace Archmap.Assist;

/// <summary>
/// Represents an edge as one location of the viewer draws it: the leaf-pair edges of a changeset aggregated to the
/// blocks of that location, with their reference counts summed and their evidence merged, largest change first. The
/// ends are node identifiers, a group as <c>g:view/group</c>, or an external.
/// </summary>
public sealed record DrawnEdge(string From, string To, int Base, int Head, string Status, IReadOnlyList<PairChange> Evidence)
{
    /// <summary>Gets the key that the edge's <see cref="Narrative"/> is filed under.</summary>
    public string Key => Narrative.EdgeKey(From, To);
}

/// <summary>
/// Represents one request for narratives: the changed edges and nodes of one level or one view, or of part of one
/// when it has more than a batch holds, or the summary of the whole changeset. See <c>docs/review-workflow.md</c>
/// section 6.2.
/// </summary>
/// <param name="Id">Identifies the batch within its plan, such as <c>summary</c>, <c>level:root/1</c> or <c>view:by-concern/1</c>.</param>
/// <param name="Kind">One of <c>summary</c>, <c>level</c> and <c>view</c>.</param>
/// <param name="Location">The level or view whose blocks the batch explains, or <see cref="Identifiers.Root"/> for the summary.</param>
/// <param name="Edges">The changed edges the batch explains.</param>
/// <param name="Nodes">The changed nodes the batch explains, each a child of the level.</param>
public sealed record NarrativeBatch(string Id, string Kind, string Location, IReadOnlyList<DrawnEdge> Edges, IReadOnlyList<string> Nodes)
{
    /// <summary>Gets the keys of the narratives the batch asks for.</summary>
    public IEnumerable<string> Keys => Kind == "summary" ? [Narrative.SummaryKey] : Edges.Select(e => e.Key).Concat(Nodes.Select(Narrative.NodeKey));
}

/// <summary>
/// Decides what a changeset's narratives explain and in which order they are requested. Each level of the map draws
/// its children, and each view draws its groups, with the changeset's leaf-pair edges aggregated to them as the
/// viewer aggregates them. An edge between two blocks is the same edge wherever it is drawn, so it is explained once,
/// at the location where both of its ends are blocks. See <c>docs/review-workflow.md</c> section 6.1.
/// </summary>
public static class NarrativePlanner
{
    /// <summary>The most edges and nodes one batch asks about. A location with more is split into several batches.</summary>
    public const int BatchSize = 24;

    /// <summary>
    /// Returns the batches of a changeset in the order they are requested: the summary, then the location the viewer
    /// opens on, which is the top level's first view or else the top level, and then the other levels and views
    /// breadth first, larger changes first within each depth. Locations with no changed edge or node are left out.
    /// </summary>
    public static List<NarrativeBatch> Plan(Changeset changeset, int batchSize = BatchSize)
    {
        var map = changeset.Map;
        var home = map.Views.FirstOrDefault(v => v.Value.Level == Identifiers.Root).Key;
        var locations = new List<(string Kind, string Id, int Depth, List<DrawnEdge> Edges, List<string> Nodes)>();
        foreach (var level in Levels(map))
        {
            var children = Children(changeset, level);
            var edges = Edges(changeset, Owners(changeset, children), externals: level == Identifiers.Root);
            var nodes = children.Where(c => changeset.Content.TryGetValue(c, out var content) && content.Status != "unchanged")
                .OrderByDescending(c => Changed(changeset.Content[c])).ThenBy(c => c, StringComparer.Ordinal).ToList();
            locations.Add(("level", level, Depth(level), edges, nodes));
        }
        foreach (var (id, view) in map.Views)
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (groupId, group) in view.Groups)
                foreach (var leaf in group.Leaves) owners[leaf] = GroupKey(id, groupId);
            locations.Add(("view", id, Depth(view.Level), Edges(changeset, owners, externals: false), []));
        }

        var ordered = locations
            .Where(l => l.Edges.Count + l.Nodes.Count > 0)
            .OrderBy(l => l.Kind == "view" && l.Id == home ? 0 : 1)
            .ThenBy(l => home is null && l.Kind == "level" && l.Id == Identifiers.Root ? 0 : 1)
            .ThenBy(l => l.Depth)
            .ThenByDescending(l => l.Edges.Sum(e => Math.Abs(e.Head - e.Base)) + l.Nodes.Sum(n => Changed(changeset.Content[n])))
            .ThenBy(l => l.Kind, StringComparer.Ordinal).ThenBy(l => l.Id, StringComparer.Ordinal);

        var batches = new List<NarrativeBatch> { new(Narrative.SummaryKey, "summary", Identifiers.Root, [], []) };
        foreach (var l in ordered)
        {
            var items = l.Edges.Select(e => (Edge: (DrawnEdge?)e, Node: (string?)null)).Concat(l.Nodes.Select(n => (Edge: (DrawnEdge?)null, Node: (string?)n))).ToList();
            for (var i = 0; i < items.Count; i += batchSize)
            {
                var part = items.Skip(i).Take(batchSize).ToList();
                batches.Add(new NarrativeBatch($"{l.Kind}:{l.Id}/{i / batchSize + 1}", l.Kind, l.Id,
                    part.Where(p => p.Edge is not null).Select(p => p.Edge!).ToList(), part.Where(p => p.Node is not null).Select(p => p.Node!).ToList()));
            }
        }
        return batches;
    }

    /// <summary>Returns the key the viewer gives a group of a view: <c>g:view/group</c>.</summary>
    public static string GroupKey(string view, string group) => $"g:{view}/{group}";

    /// <summary>
    /// Returns the edges among a set of blocks, and from them to externals when asked, that changed between base and
    /// head. Each leaf belongs to the block named for it in <paramref name="owners"/>, and an edge whose ends belong to
    /// one block, or whose source belongs to none, is not drawn.
    /// </summary>
    internal static List<DrawnEdge> Edges(Changeset changeset, IReadOnlyDictionary<string, string> owners, bool externals)
    {
        var drawn = new Dictionary<(string, string), List<EdgeChange>>();
        foreach (var e in changeset.Edges)
        {
            if (!owners.TryGetValue(e.From, out var from)) continue;
            string to;
            if (owners.TryGetValue(e.To, out var owner)) to = owner;
            else if (externals && changeset.Map.Externals.ContainsKey(e.To)) to = e.To;
            else continue;
            if (from == to) continue;
            if (!drawn.TryGetValue((from, to), out var members)) drawn[(from, to)] = members = [];
            members.Add(e);
        }
        var result = new List<DrawnEdge>();
        foreach (var ((from, to), members) in drawn)
        {
            var (b, h) = (members.Sum(m => m.Base), members.Sum(m => m.Head));
            var status = b == 0 ? "added" : h == 0 ? "removed" : b != h || members.Any(m => m.Status != "unchanged") ? "modified" : "unchanged";
            if (status == "unchanged") continue;
            var evidence = members.SelectMany(m => m.Evidence ?? [])
                .OrderByDescending(p => Math.Abs(p.Head - p.Base)).ThenBy(p => p.From, StringComparer.Ordinal).ThenBy(p => p.To, StringComparer.Ordinal).ToList();
            result.Add(new DrawnEdge(from, to, b, h, status, evidence));
        }
        return result.OrderByDescending(e => Math.Abs(e.Head - e.Base)).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
    }

    /// <summary>Returns the top level and every container of a map.</summary>
    private static IEnumerable<string> Levels(ArchMap map) =>
        new[] { Identifiers.Root }.Concat(map.Nodes.Where(n => !n.Value.IsLeaf).Select(n => n.Key));

    /// <summary>Returns the nodes a level draws as blocks: its child nodes, and at the top level the unsorted leaf when the changeset has one.</summary>
    private static List<string> Children(Changeset changeset, string level)
    {
        var children = changeset.Map.Nodes.Keys.Where(id => Identifiers.ParentOrRoot(id) == level).ToList();
        if (level == Identifiers.Root && changeset.Types.Any(t => t.LeafHead == ArchMap.Unsorted || t.LeafBase == ArchMap.Unsorted)) children.Add(ArchMap.Unsorted);
        return children;
    }

    /// <summary>Returns the block each leaf of the changeset belongs to among a level's children.</summary>
    private static Dictionary<string, string> Owners(Changeset changeset, List<string> children)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var leaves = changeset.Edges.SelectMany(e => new[] { e.From, e.To }).Distinct(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            var child = children.FirstOrDefault(c => Identifiers.IsUnder(leaf, c));
            if (child is not null) owners[leaf] = child;
        }
        return owners;
    }

    private static int Depth(string level) => level == Identifiers.Root ? 0 : level.Count(c => c == '.') + 1;

    private static int Changed(NodeContent content) => content.Added + content.Removed + content.Modified;
}
