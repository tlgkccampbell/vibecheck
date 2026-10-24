using Vibecheck.Model;

namespace Vibecheck.Diffing;

/// <summary>
/// Represents an edge as one location of the viewer draws it: the leaf-pair edges of a changeset aggregated to the
/// blocks of that location, with their reference counts summed and their evidence merged, largest change first. The
/// ends are node identifiers, a group as <c>g:view/group</c>, or an external.
/// </summary>
public sealed record DrawnEdge(string From, string To, int Base, int Head, string Status, IReadOnlyList<PairChange> Evidence)
{
    /// <summary>Gets the key the edge is filed under wherever one thing per drawn edge is kept, such as its narrative.</summary>
    public string Key => KeyOf(From, To);

    /// <summary>Returns the key of the edge between two blocks, such as <c>edge:a.x→b</c>.</summary>
    public static string KeyOf(string from, string to) => $"edge:{from}→{to}";
}

/// <summary>
/// Aggregates a changeset's leaf-pair edges to the blocks of a level or a view, as the viewer draws them. The
/// narratives and the reading path both read the drawing this way, so it is computed here, where neither depends on
/// the other.
/// </summary>
public static class DrawnEdges
{
    /// <summary>Returns the key the viewer gives a group of a view: <c>g:view/group</c>.</summary>
    public static string GroupKey(string view, string group) => $"g:{view}/{group}";

    /// <summary>
    /// Returns the edges among a set of blocks, and from them to externals when asked, that changed between base and
    /// head. Each leaf belongs to the block named for it in <paramref name="owners"/>, and an edge whose ends belong to
    /// one block, or whose source belongs to none, is not drawn.
    /// </summary>
    public static List<DrawnEdge> Edges(Changeset changeset, IReadOnlyDictionary<string, string> owners, bool externals)
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

    /// <summary>Returns the nodes a level draws as blocks: its child nodes, and at the top level the unsorted leaf when the changeset has one.</summary>
    public static List<string> Children(Changeset changeset, string level)
    {
        var children = changeset.Map.Nodes.Keys.Where(id => Identifiers.ParentOrRoot(id) == level).ToList();
        if (level == Identifiers.Root && changeset.Types.Any(t => t.LeafHead == ArchitectureMap.Unsorted || t.LeafBase == ArchitectureMap.Unsorted)) children.Add(ArchitectureMap.Unsorted);
        return children;
    }

    /// <summary>Returns the block each leaf of the changeset belongs to among a level's children.</summary>
    public static Dictionary<string, string> Owners(Changeset changeset, List<string> children)
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
}
