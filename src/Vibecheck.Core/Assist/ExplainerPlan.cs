using Vibecheck.Diffing;
using Vibecheck.Model;

namespace Vibecheck.Assist;

/// <summary>
/// Represents what a slide of an explainer points at, as the viewer opens it. The kind is one of <c>level</c>,
/// <c>edge</c>, <c>block</c>, <c>decision</c>, <c>hunk</c>, <c>type</c>, <c>pattern</c>, <c>concept</c> and
/// <c>tests</c>, and the other members are set as the kind needs them. See <c>docs/explainers.md</c> section 3.2.
/// </summary>
/// <param name="Kind">The kind of thing the target names.</param>
/// <param name="Id">
/// The identifier the viewer opens: a level's node identifier, <see cref="Identifiers.Root"/> or <c>view:</c> and a view's
/// identifier for a level; <c>from→to</c> for an edge; a node identifier for a block; a decision's identifier; a file
/// and a hunk's index joined by <c>#</c> for a hunk; a type's full name for a type, a pattern's instance or a test
/// type; and a term's key for a concept.
/// </param>
public sealed record ExplainerTarget(string Kind, string Id)
{
    /// <summary>Gets the level or view an edge is drawn at, as <see cref="PathStep.Location"/> gives it, or <see langword="null"/> for any other kind.</summary>
    public string? Location { get; init; }

    /// <summary>Gets the file of a hunk, or <see langword="null"/> for any other kind.</summary>
    public string? File { get; init; }

    /// <summary>Gets the index of a hunk among its file's hunks, or <see langword="null"/> for any other kind.</summary>
    public int? Index { get; init; }

    /// <summary>Gets the blocks a level target brings out, or <see langword="null"/> when it brings out none.</summary>
    public IReadOnlyList<string>? Blocks { get; init; }

    /// <summary>Gets the identifier of the pattern a pattern target's instance belongs to, or <see langword="null"/> for any other kind.</summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// Gets the <see cref="PathStep.Id"/> of the first step of the reading path's first two tiers that opens the same
    /// target, or <see langword="null"/> when no such step does. The viewer's <b>Open on the path</b> opens that step.
    /// </summary>
    public string? Step { get; init; }
}

/// <summary>
/// Represents one thing an explainer's slide may point at: the identifier Claude writes for it, the target the viewer
/// opens, the words the prompt lists it with, and what the completeness check of <see cref="ExplainerWriter"/> reads.
/// </summary>
/// <param name="Id">The identifier Claude writes, such as <c>block:a.x</c> or <c>hunk:src/A.cs#2</c>.</param>
/// <param name="Target">The target the viewer opens.</param>
/// <param name="Description">The words the prompt lists the entry with, after its identifier.</param>
public sealed record CatalogEntry(string Id, ExplainerTarget Target, string Description)
{
    /// <summary>Gets the kind of the entry's target.</summary>
    public string Kind => Target.Kind;

    /// <summary>Gets the blocks a level target may bring out: a level's children or a view's groups.</summary>
    public IReadOnlyList<string> Blocks { get; init; } = [];

    /// <summary>Gets the changed files the target sits in, such as a hunk's file or the files that declare a type.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Gets the nodes the target sits in, which are the blocks it covers with every block that holds them.</summary>
    public IReadOnlyList<string> Inside { get; init; } = [];

    /// <summary>Gets the type pairs behind an edge target, each as <see cref="Narrative.PairCite"/> writes it.</summary>
    public IReadOnlyList<string> Pairs { get; init; } = [];
}

/// <summary>Represents a changed file on the reading path, as the catalog lists it, with the hunks a slide may point at.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Status">The file's change status.</param>
/// <param name="Hunks">The entries of the file's hunks with edits of their own, up to <see cref="ExplainerPlanner.HunksPerFile"/>.</param>
/// <param name="More">The number of the file's other hunks with edits of their own, which the catalog leaves out.</param>
public sealed record CatalogFile(string Path, string Status, IReadOnlyList<CatalogEntry> Hunks, int More);

/// <summary>
/// Represents the catalog of an explainer: everything a slide may point at or cite, chosen from what the review already
/// put on the reading path, and what the explainer must cover. See <c>docs/explainers.md</c> section 4.1.
/// </summary>
public sealed class ExplainerCatalog
{
    /// <summary>Gets the changeset the catalog is built from.</summary>
    public required Changeset Changeset { get; init; }

    /// <summary>Gets every entry, by the identifier Claude writes for it.</summary>
    public required IReadOnlyDictionary<string, CatalogEntry> Entries { get; init; }

    /// <summary>Gets the steps of the reading path's first tier, which the explainer must cover.</summary>
    public required IReadOnlyList<PathStep> FirstTier { get; init; }

    /// <summary>Gets the steps of the reading path's second tier.</summary>
    public required IReadOnlyList<PathStep> SecondTier { get; init; }

    /// <summary>Gets the identifier of the entry each step targets, by the step's identifier, for the steps that have one.</summary>
    public required IReadOnlyDictionary<string, string> StepTargets { get; init; }

    /// <summary>Gets the type pairs each edge or cycle step names, by the step's identifier, each as <see cref="Narrative.PairCite"/> writes it.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> StepPairs { get; init; }

    /// <summary>Gets the block each decision or file step sits under, by the step's identifier, for the steps that sit under one.</summary>
    public required IReadOnlyDictionary<string, string> StepBlocks { get; init; }

    /// <summary>Gets the level and view entries, in the order the narratives explain them.</summary>
    public required IReadOnlyList<CatalogEntry> Levels { get; init; }

    /// <summary>Gets the changed edges the levels and views draw, and the edges that close the path's cycles.</summary>
    public required IReadOnlyList<CatalogEntry> Edges { get; init; }

    /// <summary>Gets the changed blocks, which the explainer must cover, largest change first within each level.</summary>
    public required IReadOnlyList<CatalogEntry> Blocks { get; init; }

    /// <summary>Gets the decisions on the reading path, in its order.</summary>
    public required IReadOnlyList<CatalogEntry> Decisions { get; init; }

    /// <summary>Gets the changed files on the reading path, in its order, with their hunks.</summary>
    public required IReadOnlyList<CatalogFile> Files { get; init; }

    /// <summary>Gets the types the change added or moved that a file on the path declares.</summary>
    public required IReadOnlyList<CatalogEntry> Types { get; init; }

    /// <summary>Gets the concepts new in the change.</summary>
    public required IReadOnlyList<CatalogEntry> Concepts { get; init; }

    /// <summary>Gets the files a slide may cite: every changed file that git reports.</summary>
    public required IReadOnlySet<string> CitableFiles { get; init; }

    /// <summary>Gets the type pairs a slide may cite: the evidence of every changed edge, each as <see cref="Narrative.PairCite"/> writes it.</summary>
    public required IReadOnlySet<string> CitablePairs { get; init; }
}

/// <summary>
/// Builds the catalog of an explainer from a changeset. The catalog is built from what the review already chose as worth
/// reading: the levels and views with changes, the first two tiers of the reading path, the hunks of the files on the
/// path, the types the change added or moved and the concepts new in it. A file the changeset sets aside as trivial or
/// generated is not listed, and neither is a file that only the analysis lists, which git never reported. See
/// <c>docs/explainers.md</c> section 4.1.
/// </summary>
public static class ExplainerPlanner
{
    /// <summary>The most hunks the catalog lists for one file.</summary>
    public const int HunksPerFile = 12;

    /// <summary>Returns the catalog of a changeset. It is a pure function of the changeset.</summary>
    public static ExplainerCatalog Plan(Changeset cs)
    {
        var map = cs.Map;
        var hunksOf = cs.Hunks.ToDictionary(h => h.Path, StringComparer.Ordinal);
        var changed = cs.Files.Where(f => f.Status != "unchanged" && hunksOf.ContainsKey(f.Path)).ToList();
        var fileOf = changed.GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // A file is listed when git reported it and the changeset does not set it aside, as the reading path decides.
        var listable = changed.Where(f => hunksOf[f.Path] is var h && h.Reason is null && h.Trivial != true
            && !(f.Status == "renamed" && f.BlobBase is not null && f.BlobBase == f.BlobHead)).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        List<string> LeavesOf(string path) => fileOf.TryGetValue(path, out var f) ? ReadingPath.CountedLeaves(f) : [];
        bool Holds(string block) => listable.Any(p => LeavesOf(p).Any(l => Identifiers.IsUnder(l, block)));

        var entries = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        CatalogEntry Add(CatalogEntry e) => entries.TryAdd(e.Id, e) ? e : entries[e.Id];
        string? InsideOf(string level) => level == Identifiers.Root ? null : level;

        var steps = cs.Path.Where(s => s.Tier is "first" or "then" && s.Kind != "more").ToList();
        var locations = NarrativePlanner.Locations(cs);

        // The changed blocks are the changed children of the levels and the blocks the path names, each with a listed file under it.
        var blockIds = locations.Where(l => l.Kind == "level").SelectMany(l => l.Nodes)
            .Concat(steps.Where(s => s.Kind == "node").Select(s => s.Target))
            .Distinct(StringComparer.Ordinal).Where(Holds).ToList();
        var blocks = blockIds.Select(id =>
        {
            var c = cs.Content.GetValueOrDefault(id);
            var node = map.Nodes.GetValueOrDefault(id);
            var what = c is null ? "" : $"; {c.Added} files added, {c.Removed} removed and {c.Modified} modified";
            var purpose = node?.Purpose is { } p ? $", {p.TrimEnd('.')}" : "";
            return Add(new CatalogEntry("block:" + id, new ExplainerTarget("block", id), $"{NarrativeWriter.NameOf(map, id)}{purpose}{what}") { Inside = [id] });
        }).ToList();
        var changedBlocks = blockIds.ToHashSet(StringComparer.Ordinal);

        // The levels and views, each with its blocks, and the changed edges each draws.
        var levels = new List<CatalogEntry>();
        var edges = new List<CatalogEntry>();
        var listedViews = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in locations)
        {
            string location;
            if (l.Kind == "level")
            {
                location = l.Id;
                var children = DrawnEdges.Children(cs, l.Id);
                var where = l.Id == Identifiers.Root ? "the top level" : $"the inside of {NarrativeWriter.NameOf(map, l.Id)}";
                levels.Add(Add(new CatalogEntry("level:" + l.Id, new ExplainerTarget("level", l.Id), $"{where}; its blocks: {BlockList(children, changedBlocks.Contains)}")
                {
                    Blocks = children, Inside = InsideOf(l.Id) is { } i ? [i] : [],
                }));
            }
            else
            {
                location = "view:" + l.Id;
                levels.Add(Add(ViewEntry(cs, l.Id, changedBlocks)));
                listedViews.Add(l.Id);
            }
            foreach (var e in l.Edges) edges.Add(Add(EdgeEntry(map, e, location)));
        }
        // A view that draws no changed edge is listed when one of its groups holds a changed block.
        foreach (var (id, view) in map.Views)
        {
            if (listedViews.Contains(id) || !view.Groups.Values.Any(g => g.Leaves.Any(changedBlocks.Contains))) continue;
            levels.Add(Add(ViewEntry(cs, id, changedBlocks)));
        }

        // The steps of the path's first two tiers, each as its target. A cycle is listed as the edge that closed it.
        var stepTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        var stepPairs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var stepBlocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var decisions = new List<CatalogEntry>();
        var decisionAt = cs.Decisions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var typeOf = cs.Types.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var files = new List<CatalogFile>();
        var decisionsOnPath = steps.Where(s => s.Kind == "decision").Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
        var pathFiles = steps.SelectMany(s => s.Files).Where(listable.Contains).Distinct(StringComparer.Ordinal).ToList();
        foreach (var path in pathFiles)
        {
            var fh = hunksOf[path];
            var own = fh.Hunks.Select((h, i) => (Hunk: h, Index: i)).Where(x => x.Hunk.Trivial != true && x.Hunk.Follows is null).ToList();
            var listed = own.Take(HunksPerFile).Select(x =>
            {
                var id = $"{path}#{x.Index}";
                var header = x.Hunk.Header.Trim();
                var decision = decisionsOnPath.Contains(id) ? $"; the decision `decision:{id}`" : "";
                return Add(new CatalogEntry("hunk:" + id, new ExplainerTarget("hunk", id) { File = path, Index = x.Index }, $"{header}{decision}")
                {
                    Files = [path], Inside = LeavesOf(path),
                });
            }).ToList();
            files.Add(new CatalogFile(path, fileOf[path].Status, listed, own.Count - listed.Count));
        }
        foreach (var step in steps)
        {
            CatalogEntry? entry = null;
            switch (step.Kind)
            {
                case "edge":
                case "cycle":
                    var ends = step.Target.Split('→');
                    var (from, to) = (ends[0], ends[^1]);
                    if (!entries.TryGetValue(DrawnEdge.KeyOf(from, to), out entry))
                    {
                        // A cycle closed by an edge that did not change is still opened at that edge.
                        entry = Add(new CatalogEntry(DrawnEdge.KeyOf(from, to), new ExplainerTarget("edge", step.Target) { Location = step.Location },
                            $"{NarrativeWriter.NameOf(map, from)} → {NarrativeWriter.NameOf(map, to)}, the edge that closes a cycle")
                        { Inside = LocationInside(map, step.Location) });
                        edges.Add(entry);
                    }
                    stepPairs[step.Id] = entry.Pairs;
                    break;
                case "node":
                    entries.TryGetValue("block:" + step.Target, out entry);
                    break;
                case "decision":
                    if (decisionAt.TryGetValue(step.Target, out var d))
                    {
                        entry = Add(new CatalogEntry("decision:" + d.Id, new ExplainerTarget("decision", d.Id), $"in `{d.File}`: {step.Signal}")
                        {
                            Files = [d.File], Inside = LeavesOf(d.File),
                        });
                        decisions.Add(entry);
                    }
                    break;
                case "test":
                    entry = Add(new CatalogEntry("tests:" + step.Target, new ExplainerTarget("tests", step.Target), $"the test type `{step.Target}`")
                    {
                        Files = step.Files,
                    });
                    break;
                case "pattern":
                    var leaf = typeOf.GetValueOrDefault(step.Target)?.LeafHead;
                    entry = Add(new CatalogEntry($"pattern:{step.Pattern}:{step.Target}", new ExplainerTarget("pattern", step.Target) { Pattern = step.Pattern },
                        $"the instance `{step.Target}` beside its nearest example")
                    {
                        Files = step.Files, Inside = leaf is null ? [] : [leaf],
                    });
                    break;
                case "file":
                    entry = files.FirstOrDefault(f => f.Path == step.Target)?.Hunks.FirstOrDefault();
                    break;
            }
            if (entry is not null) stepTargets[step.Id] = entry.Id;
            if (step.Kind is "decision" or "file" && (step.Under ?? step.Files.Select(LeavesOf).SelectMany(x => x).FirstOrDefault()) is { } under)
                stepBlocks[step.Id] = under;
        }

        // The types the change added or moved, each declared in a file on the path.
        var onPath = pathFiles.ToHashSet(StringComparer.Ordinal);
        var types = cs.Types.Where(t => t.Status is "added" or "moved" && t.Files.Any(onPath.Contains)).Select(t =>
        {
            var declared = t.Files.Where(listable.Contains).ToList();
            var leaf = t.LeafHead ?? t.LeafBase;
            var where = leaf is null ? "" : $" in {NarrativeWriter.NameOf(map, leaf)}";
            return Add(new CatalogEntry("type:" + t.Name, new ExplainerTarget("type", t.Name), $"{t.Status}{where}, declared in {string.Join(", ", declared.Select(f => $"`{f}`"))}")
            {
                Files = declared, Inside = leaf is null ? [] : [leaf],
            });
        }).ToList();

        // The concepts new in the change.
        var concepts = cs.Glossary.Where(t => t.New).Select(t =>
        {
            var words = t.Key.StartsWith("term:", StringComparison.Ordinal) ? t.Key[5..] : t.Key;
            var held = string.Join(", ", t.Instances.Take(6).Select(i => $"`{i.Type}`"));
            return Add(new CatalogEntry("concept:" + words, new ExplainerTarget("concept", t.Key), $"\"{t.Name}\", held by {held}{(t.Instances.Count > 6 ? " and others" : "")}"));
        }).ToList();

        return new ExplainerCatalog
        {
            Changeset = cs,
            Entries = entries,
            FirstTier = steps.Where(s => s.Tier == "first").ToList(),
            SecondTier = steps.Where(s => s.Tier == "then").ToList(),
            StepTargets = stepTargets,
            StepPairs = stepPairs,
            StepBlocks = stepBlocks,
            Levels = levels,
            Edges = edges,
            Blocks = blocks,
            Decisions = decisions,
            Files = files,
            Types = types,
            Concepts = concepts,
            CitableFiles = changed.Select(f => f.Path).ToHashSet(StringComparer.Ordinal),
            CitablePairs = cs.Edges.Where(e => e.Status != "unchanged").SelectMany(e => e.Evidence ?? []).Select(p => Narrative.PairCite(p.From, p.To)).ToHashSet(StringComparer.Ordinal),
        };
    }

    /// <summary>Returns the entry of a view, with its groups as the blocks it may bring out.</summary>
    private static CatalogEntry ViewEntry(Changeset cs, string id, HashSet<string> changedBlocks)
    {
        var view = cs.Map.Views[id];
        var groups = view.Groups.Keys.Select(g => DrawnEdges.GroupKey(id, g)).ToList();
        var holds = view.Groups.ToDictionary(g => DrawnEdges.GroupKey(id, g.Key), g => g.Value.Leaves.Any(changedBlocks.Contains), StringComparer.Ordinal);
        var of = view.Level == Identifiers.Root ? "the top level" : NarrativeWriter.NameOf(cs.Map, view.Level);
        return new CatalogEntry("level:view:" + id, new ExplainerTarget("level", "view:" + id), $"the view \"{view.Name}\" of {of}; its groups: {BlockList(groups, g => holds[g])}")
        {
            Blocks = groups, Inside = LocationInside(cs.Map, "view:" + id),
        };
    }

    /// <summary>Returns the entry of a changed edge drawn at a level or a view.</summary>
    private static CatalogEntry EdgeEntry(ArchitectureMap map, DrawnEdge e, string location)
    {
        var at = location == Identifiers.Root ? "the top level" : location.StartsWith("view:", StringComparison.Ordinal) ? $"the view `{location}`" : $"the inside of {NarrativeWriter.NameOf(map, location)}";
        return new CatalogEntry(e.Key, new ExplainerTarget("edge", $"{e.From}→{e.To}") { Location = location },
            $"{NarrativeWriter.NameOf(map, e.From)} → {NarrativeWriter.NameOf(map, e.To)}, drawn at {at}: {e.Status}, {e.Base} → {e.Head} references")
        {
            Pairs = e.Evidence.Select(p => Narrative.PairCite(p.From, p.To)).ToList(), Inside = LocationInside(map, location),
        };
    }

    /// <summary>Returns the node a level or view location sits inside: the level itself, or a view's level, and none for the top level.</summary>
    private static List<string> LocationInside(ArchitectureMap map, string? location)
    {
        var level = location is null ? null
            : location.StartsWith("view:", StringComparison.Ordinal) ? map.Views.GetValueOrDefault(location[5..])?.Level
            : location;
        return level is null || level == Identifiers.Root ? [] : [level];
    }

    private static string BlockList(IEnumerable<string> ids, Func<string, bool> changed) =>
        string.Join(", ", ids.Select(id => $"`{id}`" + (changed(id) ? " (changed)" : "")));
}
