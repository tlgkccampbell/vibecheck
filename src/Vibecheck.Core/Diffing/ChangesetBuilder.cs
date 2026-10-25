using Vibecheck.Checking;
using Vibecheck.Model;

namespace Vibecheck.Diffing;

/// <summary>Combines a base and a head facts document into a changeset document.</summary>
public static class ChangesetBuilder
{
    /// <summary>
    /// Combines a base and a head facts document into a changeset document, with every file that git reports as changed
    /// between two revisions and the hunks of each, read by <see cref="GitHunks"/>. File paths are relative to
    /// <paramref name="repo"/>. Renames are paired as <paramref name="renames"/> pairs them when it is given, as
    /// <see cref="GitHunks.Reconcile"/> describes, and as git pairs them otherwise.
    /// </summary>
    public static Changeset Build(Facts baseFacts, Facts headFacts, string repo, string baseRev, string headRev, IReadOnlyDictionary<string, string>? renames = null)
    {
        var changes = GitHunks.Changes(repo, baseRev, headRev);
        if (renames is not null) changes = GitHunks.Reconcile(changes, renames);
        var texts = GitHunks.Texts(repo, changes);
        return Build(baseFacts, headFacts, GitHunks.Collect(repo, baseRev, headRev, changes, mark: false), changes, texts, paths => GitHunks.TextsAt(repo, headRev, paths));
    }

    /// <summary>
    /// Combines a base and a head facts document into a changeset document, attaching the hunks given, if any. The git
    /// changes, when given, pair renamed files and add the changed files that neither analysis lists. When the files'
    /// texts are given, the hunks are marked trivial by every rule of <see cref="TrivialEdits"/> first; otherwise they
    /// are taken as marked. The patterns carry the source of their instances and examples when <paramref name="read"/>
    /// reads the texts of files at head, and otherwise only that of files whose texts are given.
    /// </summary>
    public static Changeset Build(Facts baseFacts, Facts headFacts, List<FileHunks>? hunks = null, IReadOnlyList<GitChange>? changes = null,
        IReadOnlyDictionary<string, (string? Base, string? Head)>? texts = null, Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, string>>? read = null)
    {
        read ??= paths => paths.Where(p => texts?.GetValueOrDefault(p).Head is not null).ToDictionary(p => p, p => texts![p].Head!, StringComparer.Ordinal);
        var files = DiffFiles(baseFacts, headFacts, changes ?? []);
        var declarations = DeclarationDiff.Diff(baseFacts.Declarations, headFacts.Declarations);
        var marked = texts is null ? hunks ?? [] : TrivialEdits.Mark(hunks ?? [], texts, baseFacts, headFacts, declarations, files);
        var (decisions, clustered) = Clustering.Apply(baseFacts, headFacts, files, marked, declarations);
        var (tests, tested, signalled) = TestChanges.Build(baseFacts, headFacts, files, clustered, declarations, decisions);
        var changeset = new Changeset
        {
            Base = baseFacts.Commit,
            Head = headFacts.Commit,
            Map = headFacts.Map,
            Structural = DiffStructure(baseFacts, headFacts),
            Content = Rollup(baseFacts.Map, headFacts.Map, files),
            Files = files,
            Types = DiffTypes(baseFacts, headFacts),
            Edges = DiffEdges(baseFacts, headFacts),
            Metrics = DiffMetrics(baseFacts, headFacts),
            Levels = headFacts.Levels,
            Views = headFacts.Views,
            Hunks = signalled,
            Decisions = tested,
            Tests = tests,
            Patterns = PatternChanges.Build(baseFacts, headFacts, files, read),
            NewPatterns = PatternChanges.New(baseFacts, headFacts),
            Glossary = Glossary.Build(baseFacts, headFacts, files),
        };
        return changeset with { Path = ReadingPath.Plan(changeset, baseFacts.Levels, baseFacts.Views) };
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

    /// <summary>
    /// Returns the change of every file that either analysis lists, and of every other file that git reports as
    /// changed. A rename that git reports joins the file's entries at its two paths into one, and git's status stands
    /// for a file that the analyses find unchanged.
    /// </summary>
    private static List<FileChange> DiffFiles(Facts baseFacts, Facts headFacts, IReadOnlyList<GitChange> changes)
    {
        var baseFiles = baseFacts.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var headFiles = headFacts.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var git = changes.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var previous = changes.Where(c => c.PreviousPath is not null).Select(c => c.PreviousPath!).ToHashSet(StringComparer.Ordinal);
        var result = new List<FileChange>();
        foreach (var path in baseFiles.Keys.Union(headFiles.Keys).Union(git.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            // The base path of a rename is part of the entry at its head path.
            if (previous.Contains(path)) continue;
            baseFiles.TryGetValue(path, out var b);
            headFiles.TryGetValue(path, out var h);
            git.TryGetValue(path, out var g);
            if (g?.PreviousPath is { } old)
            {
                baseFiles.TryGetValue(old, out b);
                result.Add(new FileChange(path, "renamed", b?.Leaves ?? [], h?.Leaves ?? [])
                    { PreviousPath = old, BlobBase = g.BlobBase, BlobHead = g.BlobHead, Test = b?.Test == true || h?.Test == true, Subject = (h ?? b)?.Subject });
                continue;
            }
            var status = b is null && h is null ? g!.Status : b is null ? "added" : h is null ? "removed" : b.Hash != h.Hash ? "modified" : g?.Status ?? "unchanged";
            result.Add(new FileChange(path, status, b?.Leaves ?? [], h?.Leaves ?? [])
                { BlobBase = g?.BlobBase, BlobHead = g?.BlobHead, Test = b?.Test == true || h?.Test == true, Subject = (h ?? b)?.Subject });
        }
        return result;
    }

    private static bool Under(IReadOnlyList<string> leaves, string id) => leaves.Any(l => Identifiers.IsUnder(l, id));

    /// <summary>
    /// Returns how a file's change counts under a node: <c>added</c>, <c>removed</c> or <c>modified</c>, or null when it
    /// does not count there. A renamed file counts by the leaves it has at each end, as <see cref="NodeContent"/> says.
    /// </summary>
    private static string? StatusUnder(FileChange file, string id)
    {
        bool atBase = Under(file.LeavesBase, id), atHead = Under(file.LeavesHead, id);
        return file.Status switch
        {
            "added" => atHead ? "added" : null,
            "removed" => atBase ? "removed" : null,
            "modified" => atHead ? "modified" : null,
            "renamed" => atBase && atHead ? "modified" : atBase ? "removed" : atHead ? "added" : null,
            _ => null,
        };
    }

    /// <summary>
    /// Rolls file changes up through the hierarchy. A file counts under every node that contains any leaf its types bind
    /// to, as <see cref="StatusUnder"/> decides, and a changed test file counts apart under every node that contains its
    /// subject leaf.
    /// </summary>
    private static SortedDictionary<string, NodeContent> Rollup(ArchitectureMap baseMap, ArchitectureMap headMap, List<FileChange> files)
    {
        var content = new SortedDictionary<string, NodeContent>(StringComparer.Ordinal);
        var ids = new[] { Identifiers.Root }.Concat(headMap.Nodes.Keys.Union(baseMap.Nodes.Keys)).ToList();
        if (files.Any(f => f.LeavesBase.Contains(ArchitectureMap.Unsorted) || f.LeavesHead.Contains(ArchitectureMap.Unsorted))) ids.Add(ArchitectureMap.Unsorted);
        foreach (var id in ids.Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            var total = files.Count(f => Under(f.LeavesHead, id));
            var counted = files.Select(f => StatusUnder(f, id)).ToList();
            var added = counted.Count(s => s == "added");
            var removed = counted.Count(s => s == "removed");
            var modified = counted.Count(s => s == "modified");
            var tests = files.Count(f => f.Test && f.Status != "unchanged" && (id == Identifiers.Root || (f.Subject is not null && Identifiers.IsUnder(f.Subject, id))));
            string status;
            if (id != Identifiers.Root && id != ArchitectureMap.Unsorted && !baseMap.Nodes.ContainsKey(id)) status = "added";
            else if (id != Identifiers.Root && id != ArchitectureMap.Unsorted && !headMap.Nodes.ContainsKey(id)) status = "removed";
            else if (added + removed + modified == 0) status = "unchanged";
            else status = headMap.IsContainer(id) ? "children" : "modified";
            content[id] = new NodeContent(total, added, removed, modified, status, tests);
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
