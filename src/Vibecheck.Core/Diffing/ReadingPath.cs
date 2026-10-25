using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Model;

namespace Vibecheck.Diffing;

/// <summary>
/// One step of the reading path, as <c>docs/attention.md</c> section 6 describes it. The tier is <c>first</c> for a
/// surprise, <c>then</c> for the rest of the change, or <c>aside</c> for what the reviewer need not read. The kind is
/// <c>edge</c>, <c>cycle</c>, <c>node</c>, <c>decision</c>, <c>test</c>, <c>tests</c>, <c>pattern</c>, <c>file</c>,
/// <c>more</c> for blocks the path had no room for, or <c>aside</c>. The target names what the step opens: an edge or a
/// cycle's closing edge as <c>from→to</c> between the blocks of its location, a block, a decision, a test type, the
/// instance of a pattern, or a file. The signal says in plain words
/// why the step is where it is, and the files are the changed files the step names, which make it read when they are
/// all reviewed.
/// </summary>
public sealed record PathStep(string Id, string Tier, string Kind, string Target, string Signal, List<string> Files)
{
    /// <summary>Gets the level an edge or cycle step is drawn at, or <c>view:</c> and the view's identifier for a cycle among a view's groups.</summary>
    public string? Location { get; init; }

    /// <summary>Gets the block a decision or file step sits under, when it sits under one.</summary>
    public string? Under { get; init; }

    /// <summary>Gets the members of a cycle step.</summary>
    public List<string>? Members { get; init; }

    /// <summary>Gets the identifier of the pattern whose norms a pattern step's instance departs from.</summary>
    public string? Pattern { get; init; }
}

/// <summary>
/// Plans the order in which a reviewer reads a changeset: the surprises first, each with the signal that put it there,
/// then every other changed block with the decisions and files under it, and last what is set aside. The plan is a
/// pure function of the changeset and the cycles at base, so the same changeset always gives the same path. See
/// <c>docs/attention.md</c> section 6.
/// </summary>
public static class ReadingPath
{
    /// <summary>The most steps the first two tiers hold. A changeset with more lists the rest under its blocks as counts.</summary>
    public const int MostSteps = 40;

    private static readonly HashSet<string> Resting = new(["foundation", "domain"], StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ChangeText = new(StringComparer.Ordinal)
    {
        ["added"] = "added", ["removed"] = "removed", ["reshaped"] = "signature of", ["renamed"] = "renamed", ["moved"] = "moved",
    };

    /// <summary>Returns the steps of the path, in reading order.</summary>
    /// <param name="cs">The changeset, with its decisions and hunks.</param>
    /// <param name="baseLevels">The levels at base, whose cycles tell which cycles at head are new.</param>
    /// <param name="baseViews">The views at base, whose cycles among groups tell which are new.</param>
    public static List<PathStep> Plan(Changeset cs, IReadOnlyDictionary<string, LevelMetrics> baseLevels, IReadOnlyDictionary<string, ViewMetrics> baseViews)
    {
        var map = cs.Map;
        var changed = cs.Files.Where(f => f.Status != "unchanged").ToList();
        var changedPaths = changed.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var testPaths = changed.Where(f => f.Test).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var filesOfType = cs.Types.ToDictionary(t => t.Name, t => t.Files.Where(changedPaths.Contains).ToList(), StringComparer.Ordinal);
        var hunksOf = cs.Hunks.ToDictionary(h => h.Path, StringComparer.Ordinal);
        var decisionAt = cs.Decisions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var levels = new[] { Identifiers.Root }.Concat(map.Nodes.Where(n => !n.Value.IsLeaf).Select(n => n.Key)).ToList();

        string Name(string id) => id == ArchitectureMap.Unsorted ? "Unsorted"
            : id.StartsWith("g:", StringComparison.Ordinal) && id.Split('/') is [var v, var g] && map.Views.TryGetValue(v[2..], out var view) && view.Groups.TryGetValue(g, out var group) ? group.Name
            : map.Nodes.TryGetValue(id, out var node) ? node.Name : id;
        List<string> FilesUnder(string id) => changed.Where(f => CountedLeaves(f).Any(l => Identifiers.IsUnder(l, id))).Select(f => f.Path).ToList();
        // The changed test files whose subject is a leaf, which its step lists after its own files. See docs/tests.md section 7.2.
        List<string> TestFilesOf(string leaf) => changed.Where(f => f.Test && f.Subject == leaf).Select(f => f.Path).ToList();
        List<string> EdgeFiles(DrawnEdge e)
        {
            var from = e.Evidence.SelectMany(p => filesOfType.GetValueOrDefault(p.From, [])).Distinct(StringComparer.Ordinal).ToList();
            return from.Count > 0 ? from : e.Evidence.SelectMany(p => filesOfType.GetValueOrDefault(p.To, [])).Distinct(StringComparer.Ordinal).ToList();
        }
        var first = new List<PathStep>();
        var named = new HashSet<string>(StringComparer.Ordinal);

        // New edges between the blocks of any level, those out of a block the rest of the system rests on first, then
        // the largest first.
        var newEdges = new List<(string Level, DrawnEdge Edge, bool Resting)>();
        foreach (var level in levels)
        {
            var children = DrawnEdges.Children(cs, level);
            foreach (var e in DrawnEdges.Edges(cs, DrawnEdges.Owners(cs, children), externals: false).Where(e => e.Status == "added"))
            {
                var fromCategory = map.CategoryOf(e.From);
                newEdges.Add((level, e, fromCategory is not null && Resting.Contains(fromCategory) && map.CategoryOf(e.To) != fromCategory));
            }
        }
        newEdges = newEdges.OrderByDescending(x => x.Resting).ThenByDescending(x => x.Edge.Head).ThenBy(x => x.Edge.From, StringComparer.Ordinal).ThenBy(x => x.Edge.To, StringComparer.Ordinal).ToList();

        // New cycles among a level's children or a view's groups, each as the edge that closed it.
        var cycles = new List<PathStep>();
        var closing = new HashSet<(string, string, string)>();
        foreach (var level in levels)
        {
            if (!cs.Levels.TryGetValue(level, out var metrics)) continue;
            var children = DrawnEdges.Children(cs, level);
            var added = DrawnEdges.Edges(cs, DrawnEdges.Owners(cs, children), externals: false).Where(e => e.Status == "added").ToList();
            foreach (var cycle in NewCycles(metrics.Cycles, baseLevels.TryGetValue(level, out var b) ? b.Cycles : []))
                AddCycle(level, level, cycle, added, metrics.Edges);
        }
        foreach (var (id, metrics) in cs.Views)
        {
            if (!map.Views.TryGetValue(id, out var view)) continue;
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (groupId, group) in view.Groups) foreach (var leaf in group.Leaves) owners[leaf] = DrawnEdges.GroupKey(id, groupId);
            var added = DrawnEdges.Edges(cs, owners, externals: false).Where(e => e.Status == "added").ToList();
            var edges = metrics.Edges.Select(e => new EdgeCount(DrawnEdges.GroupKey(id, e.From), DrawnEdges.GroupKey(id, e.To), e.Count)).ToList();
            foreach (var cycle in NewCycles(metrics.Cycles, baseViews.TryGetValue(id, out var b) ? b.Cycles : []))
                AddCycle("view:" + id, id, cycle.Select(g => DrawnEdges.GroupKey(id, g)).ToList(), added, edges);
        }
        void AddCycle(string location, string place, List<string> members, List<DrawnEdge> added, List<EdgeCount> edges)
        {
            var set = members.ToHashSet(StringComparer.Ordinal);
            var closer = added.Where(e => set.Contains(e.From) && set.Contains(e.To)).OrderByDescending(e => e.Head).FirstOrDefault();
            var (from, to) = closer is not null ? (closer.From, closer.To)
                : edges.Where(e => set.Contains(e.From) && set.Contains(e.To)).OrderBy(e => e.Count).Select(e => (e.From, e.To)).FirstOrDefault();
            if (from is null) return;
            closing.Add((location, from, to));
            var names = string.Join(", ", members.Select(Name));
            cycles.Add(new PathStep($"cycle:{location}:{from}→{to}", "first", "cycle", $"{from}→{to}",
                $"{names} now form a cycle, closed by {Name(from)} → {Name(to)}", closer is not null ? EdgeFiles(closer) : [])
            { Location = location, Members = members });
            foreach (var m in members) named.Add(m);
        }

        foreach (var (level, e, resting) in newEdges)
        {
            if (closing.Contains((level, e.From, e.To))) continue;
            var signal = (resting ? $"{Name(e.From)}, a {map.CategoryOf(e.From)} block, now references {Name(e.To)}" : $"{Name(e.From)} now references {Name(e.To)}")
                + $": {Plural(e.Head, "reference", "references")} in {Plural(e.Evidence.Count, "pair", "pairs")}";
            first.Add(new PathStep($"edge:{level}:{e.From}→{e.To}", "first", "edge", $"{e.From}→{e.To}", signal, EdgeFiles(e)) { Location = level });
            named.Add(e.From);
            named.Add(e.To);
        }
        first.AddRange(cycles);

        // The decision whose consequences reach the most blocks, then the most files, public before private.
        var ranked = cs.Decisions.Where(d => d.Consequences.Count > 0).Select(d => (Decision: d, Rank: Reach(d))).OrderByDescending(x => x.Rank.Blocks).ThenByDescending(x => x.Rank.Files)
            .ThenByDescending(x => x.Decision.Public).ThenByDescending(x => x.Decision.Consequences.Count).ThenBy(x => x.Decision.Id, StringComparer.Ordinal).ToList();
        var widest = ranked.Count > 0 ? ranked[0].Decision : null;
        if (widest is not null) first.Add(DecisionStep(widest, "first"));

        // The changed block with the most references into it from the other blocks of its level.
        var fanIn = levels.Where(cs.Levels.ContainsKey).SelectMany(level => DrawnEdges.Children(cs, level)
            .Where(c => cs.Content.TryGetValue(c, out var content) && content.Status != "unchanged")
            .Select(c => (Block: c, Level: level, Into: cs.Levels[level].Edges.Where(e => e.To == c).ToList())))
            .Select(x => (x.Block, x.Level, Count: x.Into.Sum(e => e.Count), From: x.Into.Count))
            .Where(x => x.Count > 0).OrderByDescending(x => x.Count).ThenBy(x => x.Level.Length).ThenBy(x => x.Block, StringComparer.Ordinal).FirstOrDefault();
        if (fanIn.Block is not null)
        {
            var files = FilesUnder(fanIn.Block);
            first.Add(new PathStep($"node:{fanIn.Block}", "first", "node", fanIn.Block,
                $"{Name(fanIn.Block)}: {Plural(fanIn.Count, "reference", "references")} into it from {Plural(fanIn.From, "block", "blocks")}; {Plural(files.Count, "file", "files")} changed inside", files));
            named.Add(fanIn.Block);
        }

        // Each test type with a signal that touches changed code is a step after the decisions, the widest reach of the
        // decisions its signals touch first, and every other signal is gathered into one closing step of the tier. See
        // docs/tests.md section 7.2.
        var typeOf = cs.Tests.SelectMany(t => t.Methods.Select(m => (m.Symbol, Type: t))).DistinctBy(x => x.Symbol).ToDictionary(x => x.Symbol, x => x.Type, StringComparer.Ordinal);
        var signals = cs.Hunks.SelectMany(f => f.Hunks.SelectMany(h => h.Signals ?? [])).Where(s => typeOf.ContainsKey(s.Method)).ToList();
        var decisionOf = cs.Decisions.SelectMany(d => d.Declarations.Select(x => (x.Symbol, d))).DistinctBy(x => x.Symbol).ToDictionary(x => x.Symbol, x => x.d, StringComparer.Ordinal);
        (int Blocks, int Files) SignalReach(TestSignal s) => s.Touches.Select(t => decisionOf.TryGetValue(t.Symbol, out var d) ? Reach(d) : (0, 0, 0))
            .Select(r => (r.Item1, r.Item2)).DefaultIfEmpty((0, 0)).Max();
        var testSteps = signals.Where(s => s.Touches.Count > 0).GroupBy(s => typeOf[s.Method].Name, StringComparer.Ordinal)
            .Select(g => (Type: typeOf[g.First().Method], Signals: g.ToList(), Reach: g.Select(SignalReach).Max()))
            .OrderByDescending(x => x.Reach.Blocks).ThenByDescending(x => x.Reach.Files).ThenBy(x => x.Type.Name, StringComparer.Ordinal)
            .Select(x => new PathStep($"test:{x.Type.Name}", "first", "test", x.Type.Name, string.Join("; ", x.Signals.Select(SignalText)), x.Type.Files))
            .ToList();
        first.AddRange(testSteps);
        var unexplained = signals.Where(s => s.Touches.Count == 0).ToList();
        if (unexplained.Count > 0)
        {
            var methods = unexplained.GroupBy(s => s.Method, StringComparer.Ordinal).ToList();
            first.Add(new PathStep("tests:other", "first", "tests", "other",
                $"{Plural(methods.Count, "test", "tests")} changed in ways no changed declaration {(methods.Count == 1 ? "it references explains" : "they reference explains")}",
                methods.SelectMany(g => typeOf[g.Key].Files).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList())
            { Members = methods.Select(g => $"{DeclarationDiff.ShortName(g.Key, "method")}: {string.Join(", ", g.Select(s => KindText(s.Kind)).Distinct())}").ToList() });
        }

        // Each added or changed instance of a pattern that departs from the pattern's norms is a step after the tests,
        // the most departures first, then the largest pattern. See docs/patterns.md section 7.4.
        var patternSteps = cs.Patterns.SelectMany(p => p.Instances.Where(i => i.Departures.Count > 0).Select(i => (Pattern: p, Instance: i)))
            .OrderByDescending(x => x.Instance.Departures.Count).ThenByDescending(x => x.Pattern.Count).ThenBy(x => x.Instance.Type, StringComparer.Ordinal)
            .Select(x => new PathStep($"pattern:{x.Pattern.Id}:{x.Instance.Type}", "first", "pattern", x.Instance.Type,
                $"{DeclarationDiff.ShortName(x.Instance.Type, "type")} departs from {x.Pattern.Name}: "
                    + string.Join(", ", x.Instance.Departures.Select(f => x.Pattern.Norms.First(n => n.Feature == f).Absence)),
                filesOfType.GetValueOrDefault(x.Instance.Type, [])) { Pattern = x.Pattern.Id })
            .ToList();
        first.AddRange(patternSteps);

        // Set aside: files whose edits are trivial or generated, and files whose hunks are all consequences. They are
        // found first, so that the second tier lists neither them nor a block that holds nothing else.
        var trivial = changed.Where(f => hunksOf.GetValueOrDefault(f.Path)?.Reason is not null || (f.Status == "renamed" && f.BlobBase is not null && f.BlobBase == f.BlobHead)).Select(f => f.Path).ToList();
        var consequences = changed.Where(f => !trivial.Contains(f.Path) && ConsequenceOnly(hunksOf.GetValueOrDefault(f.Path), decisionAt)).Select(f => f.Path).ToList();
        var setAside = trivial.Concat(consequences).ToHashSet(StringComparer.Ordinal);

        // Every other changed leaf, largest change first within each level, with the decisions and files under it.
        var then = new List<PathStep>();
        var leaves = new List<(string Leaf, string Level, int Size)>();
        foreach (var level in levels)
        {
            foreach (var c in DrawnEdges.Children(cs, level))
            {
                if (named.Contains(c) || map.IsContainer(c)) continue;
                var files = FilesUnder(c).Concat(TestFilesOf(c)).ToList();
                if (files.Count > 0 && !files.All(setAside.Contains)) leaves.Add((c, level, files.Count));
            }
        }
        var decided = new HashSet<string>(StringComparer.Ordinal);
        var listedDecisionFiles = new HashSet<string>(StringComparer.Ordinal);
        if (widest is not null) { decided.Add(widest.Id); listedDecisionFiles.Add(widest.File); }
        var leafSteps = new List<PathStep>();
        var listedTests = new HashSet<string>(StringComparer.Ordinal);
        var decisionSteps = new List<PathStep>();
        var fileSteps = new List<PathStep>();
        var leafOfType = cs.Types.ToDictionary(t => t.Name, t => t.LeafHead, StringComparer.Ordinal);
        foreach (var (leaf, level, _) in leaves.OrderBy(l => levels.IndexOf(l.Level)).ThenByDescending(l => l.Size).ThenBy(l => l.Leaf, StringComparer.Ordinal))
        {
            var files = FilesUnder(leaf);
            var tests = TestFilesOf(leaf);
            var decisions = ranked.Select(x => x.Decision).Concat(cs.Decisions.Where(d => d.Consequences.Count == 0))
                .Where(d => !decided.Contains(d.Id) && files.Contains(d.File)).ToList();
            var own = files.Where(p => !setAside.Contains(p) && !listedDecisionFiles.Contains(p) && !decisions.Any(d => d.File == p) && OwnEdits(hunksOf.GetValueOrDefault(p), decisionAt) > 0).ToList();
            var parts = new List<string>();
            if (files.Count > 0) parts.Add(Plural(files.Count, "file changed", "files changed"));
            if (decisions.Count > 0) parts.Add(Plural(decisions.Count, "decision", "decisions"));
            if (own.Count > 0) parts.Add($"{Plural(own.Count, "file", "files")} with edits on their own");
            if (tests.Count > 0) parts.Add(Plural(tests.Count, "test file", "test files"));
            var testSignals = tests.Sum(p => hunksOf.GetValueOrDefault(p)?.Hunks.Sum(h => h.Signals?.Count ?? 0) ?? 0);
            if (testSignals > 0) parts.Add(Plural(testSignals, "test signal", "test signals"));
            // The instances of patterns in the leaf are counted on its step, whether or not they have steps of their own.
            foreach (var p in cs.Patterns)
            {
                var mine = p.Instances.Where(i => leafOfType.GetValueOrDefault(i.Type) == leaf).ToList();
                foreach (var (status, word) in new[] { ("added", "new"), ("changed", "changed") })
                {
                    var n = mine.Count(i => i.Status == status);
                    if (n > 0) parts.Add($"{n} {word} {(n == 1 ? "instance" : "instances")} of {p.Name}");
                }
            }
            listedTests.UnionWith(tests);
            var step = new PathStep($"node:{leaf}", "then", "node", leaf, $"{Name(leaf)}: {string.Join(" · ", parts)}", [.. files, .. tests]) { Under = level == Identifiers.Root ? null : level };
            leafSteps.Add(step);
            then.Add(step);
            foreach (var d in decisions)
            {
                var ds = DecisionStep(d, "then") with { Under = leaf };
                decided.Add(d.Id);
                listedDecisionFiles.Add(d.File);
                decisionSteps.Add(ds);
                then.Add(ds);
            }
            foreach (var p in own)
            {
                var h = hunksOf.GetValueOrDefault(p);
                var total = h?.Hunks.Count ?? 0;
                var own1 = OwnEdits(h, decisionAt);
                var fs = new PathStep($"file:{p}", "then", "file", p, $"{FileName(p)}: {own1} of {Plural(total, "hunk", "hunks")} {(own1 == 1 ? "is an edit" : "are edits")} on {(own1 == 1 ? "its" : "their")} own", [p]) { Under = leaf };
                fileSteps.Add(fs);
                then.Add(fs);
            }
        }
        // Changed test files that no leaf's step lists, test infrastructure among them, and then the changed files that no
        // type binds, such as project files and documents, come last.
        var rest = changed.Where(f => f.Test && !listedTests.Contains(f.Path) && !setAside.Contains(f.Path)).OrderBy(f => f.Subject is null).ThenBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => (File: f, Where: f.Subject is null ? "test infrastructure" : $"tests of {Name(f.Subject)}"))
            .Concat(changed.Where(f => !f.Test && CountedLeaves(f).Count == 0 && hunksOf.GetValueOrDefault(f.Path)?.Reason is null).OrderBy(f => f.Path, StringComparer.Ordinal)
                .Select(f => (File: f, Where: "outside the map")));
        foreach (var (f, where) in rest)
        {
            var fs = new PathStep($"file:{f.Path}", "then", "file", f.Path, $"{FileName(f.Path)}: {f.Status}, {where}", [f.Path]);
            fileSteps.Add(fs);
            then.Add(fs);
        }

        // The path holds at most forty steps in its first two tiers. Files go first, then test steps, then the instances of
        // patterns, which their leaves' steps still count, then decisions, then whole blocks, which are kept as one count so
        // that their files still count toward the progress.
        var dropped = new List<PathStep>();
        foreach (var pool in new[] { fileSteps, testSteps, patternSteps, decisionSteps, leafSteps })
        {
            while (first.Count + then.Count > MostSteps && pool.Count > 0)
            {
                var last = pool[^1];
                pool.RemoveAt(pool.Count - 1);
                then.Remove(last);
                first.Remove(last);
                if (last.Kind == "node") dropped.Add(last);
            }
        }
        if (dropped.Count > 0)
        {
            then.Add(new PathStep("more", "then", "more", "", $"{Plural(dropped.Count, "more block", "more blocks")} changed: {string.Join(", ", dropped.AsEnumerable().Reverse().Select(s => Name(s.Target)))}",
                dropped.SelectMany(s => s.Files).Distinct(StringComparer.Ordinal).ToList()));
        }

        var aside = new List<PathStep>();
        // Says how many of the files are in the system and in how many blocks, with the test files, which count under no
        // block, apart.
        string Where(List<string> paths, string one, string many)
        {
            var system = paths.Where(p => !testPaths.Contains(p)).ToList();
            var tests = paths.Count - system.Count;
            var blocks = system.SelectMany(p => CountedLeaves(changed.First(f => f.Path == p))).Distinct(StringComparer.Ordinal).Count();
            var text = system.Count > 0 ? $"{Plural(system.Count, one, many)} in {Plural(blocks, "block", "blocks")}" : "";
            return tests == 0 ? text : text.Length == 0 ? $"{Plural(tests, "test " + one, "test " + many)}" : $"{text}, and {tests} in tests";
        }
        if (consequences.Count > 0) aside.Add(new PathStep("aside:consequences", "aside", "aside", "consequences", Where(consequences, "file of consequences", "files of consequences"), consequences));
        if (trivial.Count > 0) aside.Add(new PathStep("aside:trivial", "aside", "aside", "trivial", Where(trivial, "trivial or generated file", "trivial or generated files"), trivial));

        return first.Concat(then).Concat(aside).ToList();

        PathStep DecisionStep(Decision d, string tier)
        {
            var lead = d.Declarations.OrderByDescending(x => x.SitesBase + x.SitesHead).First();
            var reach = Reach(d);
            var what = lead.Change == "reshaped" ? $"signature of {Short(lead)} changed" : $"{ChangeText.GetValueOrDefault(lead.Change, lead.Change)} {Short(lead)}";
            var others = d.Declarations.Count - 1;
            var signal = what + (others > 0 ? $" and {Plural(others, "other", "others")}" : "") + (d.Public ? ", public" : "") + ": "
                + (d.Consequences.Count == 0 ? "no consequences"
                    : reach.Files == 0 ? $"{Plural(reach.Tests, "consequence", "consequences")} in tests"
                    : $"{Plural(d.Consequences.Count - reach.Tests, "consequence", "consequences")} in {Plural(reach.Files, "file", "files")} across {Plural(reach.Blocks, "block", "blocks")}"
                        + (reach.Tests > 0 ? $", and {reach.Tests} in tests" : ""));
            return new PathStep($"decision:{d.Id}", tier, "decision", d.Id, signal, [d.File]);
        }

        // A decision's reach counts the blocks and files of the system its consequences are in. Its consequences in test
        // files count apart, so that tests do not rank a decision above one that reaches further into the system.
        (int Blocks, int Files, int Tests) Reach(Decision d)
        {
            var tests = d.Consequences.Count(c => testPaths.Contains(c.File));
            var files = d.Consequences.Select(c => c.File).Where(p => !testPaths.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
            var blocks = files.Select(p => changed.FirstOrDefault(f => f.Path == p)).OfType<FileChange>().SelectMany(CountedLeaves).Distinct(StringComparer.Ordinal).Count();
            return (blocks, files.Count, tests);
        }
    }

    /// <summary>Returns the cycles at head whose members no cycle at base held together.</summary>
    private static IEnumerable<List<string>> NewCycles(List<List<string>> head, List<List<string>> before) =>
        head.Where(c => !before.Any(b => c.All(b.Contains)));

    /// <summary>Returns the leaves a changed file counts under, as the changeset's content counts it: its leaves at head, at base when it was removed, and at both ends when it was renamed.</summary>
    private static List<string> CountedLeaves(FileChange f) => f.Status switch
    {
        "removed" => f.LeavesBase.ToList(),
        "renamed" => f.LeavesBase.Concat(f.LeavesHead).Distinct(StringComparer.Ordinal).ToList(),
        _ => f.LeavesHead.ToList(),
    };

    /// <summary>Returns how many of a file's hunks are neither decisions, consequences nor trivial.</summary>
    private static int OwnEdits(FileHunks? file, Dictionary<string, Decision> decisions) =>
        file is null ? 0 : file.Hunks.Select((h, i) => h.Trivial != true && h.Follows is null && !decisions.ContainsKey(file.Path + "#" + i)).Count(x => x);

    /// <summary>Returns a value indicating whether every hunk of a file follows from a decision or is trivial, and at least one follows.</summary>
    private static bool ConsequenceOnly(FileHunks? file, Dictionary<string, Decision> decisions) =>
        file is not null && file.Hunks.Any(h => h.Follows is not null) && file.Hunks.Select((h, i) => h.Follows is not null || h.Trivial == true).All(x => x);

    /// <summary>Returns the name a declaration is shown by, as <see cref="DeclarationDiff.ShortName"/> gives it, with its previous name first when it was renamed or moved.</summary>
    private static string Short(DecisionDeclaration x) =>
        x.Previous is not null ? $"{DeclarationDiff.ShortName(x.Previous, x.Kind)} → {DeclarationDiff.ShortName(x.Symbol, x.Kind)}" : DeclarationDiff.ShortName(x.Symbol, x.Kind);

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>Returns the words for a kind of <see cref="TestSignal"/>.</summary>
    private static string KindText(string kind) => kind switch
    {
        TestSignal.Removed => "removed",
        TestSignal.Skipped => "skipped",
        TestSignal.AssertionRemoved => "assertion removed",
        TestSignal.ExpectedChanged => "expected value changed",
        _ => "assertion changed",
    };

    /// <summary>
    /// Returns a test signal as a fact, naming its method and, when it touches changed code, the first changed declaration
    /// the method references, such as "assertion removed from SessionTests.Require_Succeeds, which references
    /// Session.Require, rewritten".
    /// </summary>
    private static string SignalText(TestSignal s)
    {
        var method = DeclarationDiff.ShortName(s.Method, "method");
        var what = s.Kind switch
        {
            TestSignal.Removed => $"{method} removed",
            TestSignal.Skipped => $"{method} skipped",
            TestSignal.AssertionRemoved => $"assertion removed from {method}",
            TestSignal.ExpectedChanged => $"expected value changed in {method}",
            _ => $"assertion changed in {method}",
        };
        if (s.Touches.Count == 0) return what;
        var lead = s.Touches[0];
        var change = lead.Change == "reshaped" ? "signature changed" : lead.Change;
        var others = s.Touches.Count - 1;
        return $"{what}, which references {DeclarationDiff.ShortName(lead.Symbol, lead.Kind)}, {change}" + (others > 0 ? $", and {Plural(others, "other changed declaration", "other changed declarations")}" : "");
    }

    /// <summary>Returns the last segment of a path.</summary>
    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
