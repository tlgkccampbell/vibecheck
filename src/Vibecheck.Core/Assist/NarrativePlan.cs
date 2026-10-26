using Vibecheck.Diffing;
using Vibecheck.Model;

namespace Vibecheck.Assist;

/// <summary>
/// Represents one request for narratives: the changed edges and nodes of one level or one view, or of part of one
/// when it has more than a batch holds, the glossary's terms that have no documentation summary, the name and
/// purpose of one pattern, or the summary of the whole changeset. See <c>docs/review-workflow.md</c> section 6.2,
/// <c>docs/attention.md</c> section 8.4 and <c>docs/patterns.md</c> section 8.
/// </summary>
/// <param name="Id">Identifies the batch within its plan, such as <c>summary</c>, <c>level:root/1</c>, <c>view:by-concern/1</c>, <c>term:root/1</c> or <c>pattern:consumer</c>.</param>
/// <param name="Kind">One of <c>summary</c>, <c>level</c>, <c>view</c>, <c>term</c> and <c>pattern</c>.</param>
/// <param name="Location">The level or view whose blocks the batch explains, the pattern it names, or <see cref="Identifiers.Root"/> for the summary and for terms.</param>
/// <param name="Edges">The changed edges the batch explains.</param>
/// <param name="Nodes">The changed nodes the batch explains, each a child of the level.</param>
public sealed record NarrativeBatch(string Id, string Kind, string Location, IReadOnlyList<DrawnEdge> Edges, IReadOnlyList<string> Nodes)
{
    /// <summary>Gets the terms the batch defines, each a term of the glossary without a documentation summary.</summary>
    public IReadOnlyList<Term> Terms { get; init; } = [];

    /// <summary>Gets the pattern the batch names, or <see langword="null"/> for a batch of any other kind.</summary>
    public PatternChange? Pattern { get; init; }

    /// <summary>Gets the keys of the narratives the batch asks for.</summary>
    public IEnumerable<string> Keys => Kind switch
    {
        "summary" => [Narrative.SummaryKey],
        "term" => Terms.Select(t => t.Key),
        "pattern" => [Narrative.PatternKey(Pattern!.Keys)],
        _ => Edges.Select(e => e.Key).Concat(Nodes.Select(Narrative.NodeKey)),
    };
}

/// <summary>Represents a level or a view whose blocks have narratives, with the changed edges it draws and its changed children.</summary>
/// <param name="Kind">Either <c>level</c> or <c>view</c>.</param>
/// <param name="Id">The level's node identifier, <see cref="Identifiers.Root"/> for the top level, or the view's identifier.</param>
/// <param name="Edges">The changed edges the location draws between its blocks, and at the top level to externals.</param>
/// <param name="Nodes">The changed children of a level, largest change first. A view lists none.</param>
public sealed record NarrativeLocation(string Kind, string Id, IReadOnlyList<DrawnEdge> Edges, IReadOnlyList<string> Nodes);

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
    /// Returns the batches of a changeset in the order they are requested: the summary, then the name and purpose of
    /// each pattern whose map entry does not give both, then the location the viewer opens on, which is the top level's
    /// first view or else the top level, then the other levels and views breadth first, larger changes first within
    /// each depth, and last the glossary's terms without a documentation summary. Locations with no changed
    /// edge or node are left out, and so is a pattern with no instance at base to name it from.
    /// </summary>
    public static List<NarrativeBatch> Plan(Changeset changeset, int batchSize = BatchSize)
    {
        var map = changeset.Map;
        var ordered = Locations(changeset);

        var batches = new List<NarrativeBatch> { new(Narrative.SummaryKey, "summary", Identifiers.Root, [], []) };
        // A pattern's name shows on every chip of its instances, so it is asked for early, and its request is cheap.
        foreach (var p in changeset.Patterns.Where(p => p.Samples.Count > 0))
        {
            var entry = map.Patterns.FirstOrDefault(e => e.Id == p.Id);
            if (entry?.Name is null || entry.Purpose is null) batches.Add(new NarrativeBatch("pattern:" + p.Id, "pattern", p.Id, [], []) { Pattern = p });
        }
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
        // The terms with no documentation summary come last, in the glossary's order, so that the explanations the
        // reviewer meets first are written first. A term that a type's whole name documents needs no definition.
        var terms = changeset.Glossary.Where(t => t.Summary is null).ToList();
        for (var i = 0; i < terms.Count; i += batchSize)
            batches.Add(new NarrativeBatch($"term:{Identifiers.Root}/{i / batchSize + 1}", "term", Identifiers.Root, [], []) { Terms = terms.Skip(i).Take(batchSize).ToList() });
        return batches;
    }

    /// <summary>
    /// Returns the levels and views that draw a changed edge or hold a changed node, each with those edges and nodes, in
    /// the order their narratives are requested: the location the viewer opens on first, which is the top level's first
    /// view or else the top level, then the others breadth first, larger changes first within each depth. A view's
    /// groups have no narratives of their own, so a view lists no nodes.
    /// </summary>
    public static List<NarrativeLocation> Locations(Changeset changeset)
    {
        var map = changeset.Map;
        var home = map.Views.FirstOrDefault(v => v.Value.Level == Identifiers.Root).Key;
        var locations = new List<(string Kind, string Id, int Depth, List<DrawnEdge> Edges, List<string> Nodes)>();
        foreach (var level in Levels(map))
        {
            var children = DrawnEdges.Children(changeset, level);
            var edges = DrawnEdges.Edges(changeset, DrawnEdges.Owners(changeset, children), externals: level == Identifiers.Root);
            var nodes = children.Where(c => changeset.Content.TryGetValue(c, out var content) && content.Status != "unchanged")
                .OrderByDescending(c => Changed(changeset.Content[c])).ThenBy(c => c, StringComparer.Ordinal).ToList();
            locations.Add(("level", level, Depth(level), edges, nodes));
        }
        foreach (var (id, view) in map.Views)
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (groupId, group) in view.Groups)
                foreach (var leaf in group.Leaves) owners[leaf] = DrawnEdges.GroupKey(id, groupId);
            locations.Add(("view", id, Depth(view.Level), DrawnEdges.Edges(changeset, owners, externals: false), []));
        }

        return locations
            .Where(l => l.Edges.Count + l.Nodes.Count > 0)
            .OrderBy(l => l.Kind == "view" && l.Id == home ? 0 : 1)
            .ThenBy(l => home is null && l.Kind == "level" && l.Id == Identifiers.Root ? 0 : 1)
            .ThenBy(l => l.Depth)
            .ThenByDescending(l => l.Edges.Sum(e => Math.Abs(e.Head - e.Base)) + l.Nodes.Sum(n => Changed(changeset.Content[n])))
            .ThenBy(l => l.Kind, StringComparer.Ordinal).ThenBy(l => l.Id, StringComparer.Ordinal)
            .Select(l => new NarrativeLocation(l.Kind, l.Id, l.Edges, l.Nodes))
            .ToList();
    }

    /// <summary>Returns the top level and every container of a map.</summary>
    private static IEnumerable<string> Levels(ArchitectureMap map) =>
        new[] { Identifiers.Root }.Concat(map.Nodes.Where(n => !n.Value.IsLeaf).Select(n => n.Key));

    private static int Depth(string level) => level == Identifiers.Root ? 0 : level.Count(c => c == '.') + 1;

    private static int Changed(NodeContent content) => content.Added + content.Removed + content.Modified;
}
