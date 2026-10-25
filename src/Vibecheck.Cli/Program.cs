using System.Diagnostics;
using Vibecheck.Analysis;
using Vibecheck.Assist;
using Vibecheck.Bootstrap;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Format;
using Vibecheck.GitHub;
using Vibecheck.Model;
using Vibecheck.Reviewing;

namespace Vibecheck.Cli;

/// <summary>The <c>vibecheck</c> command-line tool. See <c>docs/vibecheck-spec.md</c> section 7.</summary>
public static class Program
{
    private const string Usage = """
        usage:
          vibecheck           [review options]
          vibecheck review    [<base> [<head>] | <range>] [--map <vibecheck.json>] [--root <dir>] [--solution <sln>]
                            [--no-build] [--no-cache] [--no-ai] [--no-github] [--model <model>] [--no-open] [--port <n>] [--viewer <dir>]
          vibecheck pr        <number> | <url> [review options]
          vibecheck init      --solution <path.sln> [--root <dir>] [--map <vibecheck.json>] [--facts <out.json>]
          vibecheck units     [--map <vibecheck.json>] [--root <dir>] [--out <units.txt>]
          vibecheck check     [--map <vibecheck.json>] [--root <dir>] [--facts <out.json>] [--strict]
          vibecheck why       <from> <to> [--map <vibecheck.json>] [--root <dir>] [--facts <facts.json>] [--limit <n>]
          vibecheck fmt       [--map <vibecheck.json>] [--check]
          vibecheck facts     [--map <vibecheck.json>] [--root <dir>] [--out <out.json>] [--commit <sha>]
          vibecheck diff      <base-facts.json> <head-facts.json> [--out <out.json>] [--repo <dir> --base-rev <rev> --head-rev <rev>]
          vibecheck changeset <base> [<head>] [--map <vibecheck.json>] [--root <dir>] [--out <out.json>] [--no-build] [--no-cache]
          vibecheck guide
          vibecheck intent    [<file>] [--map <vibecheck.json>] [--root <dir>] [--base <rev>]

        vibecheck with no command, or vibecheck review, opens the viewer in the browser on a range of the repository
        that the current directory is in. Without a range it opens on a picker. The map is --map when given, then the
        vibecheck.json nearest the current directory at the head revision, and otherwise the namespace skeleton.
        vibecheck pr opens straight into a pull request of the repository that gh names, fetching its commits when
        they are missing. --no-github leaves GitHub out: the picker lists no pull requests.

        The map defaults to ./vibecheck.json, and the layout file sits beside it. The repository root,
        which the map's solution path and every reported path are relative to, defaults to the map's
        directory; pass --root when the map is kept outside the repository. changeset takes two
        revisions or one range (a..b, or a...b for a pull request), and the head defaults to HEAD.

        vibecheck guide prints the guide for coding agents: how to keep the map in sync with a change, and how to state
        the change's intent in the pull request description. vibecheck intent checks the Intent section of a draft
        description, read from the file or from standard input, without building the solution. File anchors are
        paths from the top of the repository that holds the root, and --base marks the files changed since a revision.
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            // With no command, or with options alone, the tool opens a review.
            if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal) && args[0] != "--help") args = ["review", .. args];
            var (positional, options) = ParseArgs(args.Skip(1));
            return args[0] switch
            {
                "review" => await ReviewAsync(positional, options),
                "pr" => await ReviewAsync(positional, options, pull: true),
                "init" => await InitAsync(options),
                "units" => await UnitsAsync(options),
                "check" => await CheckAsync(options),
                "why" => await WhyAsync(positional, options),
                "fmt" => Fmt(options),
                "facts" => await FactsAsync(options),
                "diff" => Diff(positional, options),
                "changeset" => await ChangesetAsync(positional, options),
                "guide" => Guide(),
                "intent" => Intent(positional, options),
                "--help" or "-h" or "help" => Help(),
                _ => Unknown(args[0]),
            };
        }
        catch (MapFormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static int Help() { Console.WriteLine(Usage); return 0; }

    private static int Guide() { Console.Out.Write(AgentNote.Guide()); return 0; }

    /// <summary>
    /// Checks the intents of a draft description against the map, when there is one, and the working tree. The map is
    /// <c>--map</c>, or <c>./vibecheck.json</c> when it exists.
    /// </summary>
    private static int Intent(List<string> positional, Dictionary<string, string> options)
    {
        if (positional.Count > 1) throw new ArgumentException("intent takes at most one file");
        // Standard input is read as UTF-8, since the console's own encoding on Windows loses the arrow of an edge.
        var markdown = positional.Count == 0 || positional[0] == "-"
            ? new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8).ReadToEnd()
            : File.ReadAllText(positional[0]);
        ArchitectureMap? map = null;
        string? mapDir = null;
        if (options.ContainsKey("map") || File.Exists(MapPath(options))) (map, _, _, mapDir) = LoadMap(options);
        var dir = Path.GetFullPath(options.GetValueOrDefault("root") ?? mapDir ?? Directory.GetCurrentDirectory());
        var top = Git.TryRun(dir, "rev-parse", "--show-toplevel") is { } t ? Path.GetFullPath(t) : dir;
        return IntentCommand.Run(markdown, map, top, options.GetValueOrDefault("base"), Console.Out, Console.Error);
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command \"{command}\"\n" + Usage);
        return 2;
    }

    private static (List<string> Positional, Dictionary<string, string> Options) ParseArgs(IEnumerable<string> args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = list[i][2..];
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) options[name] = list[++i];
                else options[name] = "true";
            }
            else
            {
                positional.Add(list[i]);
            }
        }
        return (positional, options);
    }

    private static string MapPath(Dictionary<string, string> options) =>
        Path.GetFullPath(options.GetValueOrDefault("map", "vibecheck.json"));

    /// <summary>
    /// Reads the map and returns it with its text, the repository root and the map's directory. The root is the
    /// <c>--root</c> option when given and otherwise the map's directory.
    /// </summary>
    private static (ArchitectureMap Map, string Text, string Root, string MapDir) LoadMap(Dictionary<string, string> options)
    {
        var path = MapPath(options);
        if (!File.Exists(path)) throw new IOException($"map not found: {path}");
        var text = File.ReadAllText(path);
        var mapDir = Path.GetDirectoryName(path)!;
        var root = Path.GetFullPath(options.GetValueOrDefault("root", mapDir));
        if (!Directory.Exists(root)) throw new IOException($"root not found: {root}");
        return (MapReader.Read(text), text, root, mapDir);
    }

    private static async Task<AnalysisResult> AnalyzeAsync(string root, string solution)
    {
        var solutionPath = Path.GetFullPath(Path.Combine(root, solution));
        if (!File.Exists(solutionPath)) throw new IOException($"solution not found: {solutionPath}; pass --root when the map is kept outside the repository");
        var progress = new Progress<string>(m => Console.Error.WriteLine("  " + m));
        var sw = Stopwatch.StartNew();
        var result = await RoslynAnalyzer.AnalyzeAsync(root, solutionPath, progress);
        Console.Error.WriteLine($"  analyzed {result.Types.Count} types in {result.Files.Count} files, {result.Dependencies.Count} dependencies in {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var w in result.Warnings) Console.Error.WriteLine("  warning: " + w);
        return result;
    }

    private static async Task<int> InitAsync(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("solution", out var solution)) throw new ArgumentException("init requires --solution");
        var solutionPath = Path.GetFullPath(solution);
        var root = Path.GetFullPath(options.GetValueOrDefault("root", Path.GetDirectoryName(solutionPath)!));
        var mapPath = Path.GetFullPath(options.GetValueOrDefault("map", Path.Combine(root, "vibecheck.json")));
        if (File.Exists(mapPath)) throw new IOException($"{mapPath} already exists; init does not overwrite a map");

        var analysis = await AnalyzeAsync(root, solutionPath);
        var map = Skeleton.Build(analysis, Path.GetRelativePath(root, solutionPath));
        File.WriteAllText(mapPath, MapWriter.Write(map));
        Console.WriteLine($"wrote {mapPath}: {map.Nodes.Count} nodes, excluding {string.Join(", ", map.Exclude)}");
        if (Path.GetFullPath(Path.GetDirectoryName(mapPath)!) != root) Console.WriteLine($"the map is outside the root; pass --root {root} to the other commands");

        var facts = Checker.Run(map, analysis, new CheckOptions { Commit = DetectCommit(root) });
        var factsPath = options.GetValueOrDefault("facts", Path.Combine(Path.GetDirectoryName(mapPath)!, "vibecheck.facts.json"));
        File.WriteAllText(factsPath, FactsJson.Serialize(facts));
        Console.WriteLine($"wrote {factsPath}");
        var code = Report(facts, strict: false);

        // A map inside the repository can be named to the coding agents that work there.
        var mapInRoot = Path.GetRelativePath(root, mapPath);
        if (!mapInRoot.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(mapInRoot))
        {
            Console.WriteLine();
            Console.WriteLine($"To have coding agents keep the map in sync and state the intent of their changes, add this to the repository's {AgentNote.Claude} or {AgentNote.Agents}:");
            Console.WriteLine();
            Console.Write(AgentNote.Text(mapInRoot.Replace(Path.DirectorySeparatorChar, '/')));
        }
        return code;
    }

    private static async Task<int> UnitsAsync(Dictionary<string, string> options)
    {
        var (map, _, root, _) = LoadMap(options);
        var analysis = await AnalyzeAsync(root, map.Solution);
        var errors = new List<string>();
        var (included, _) = Exclusion.Parse(map, errors).Apply(analysis);
        if (errors.Count > 0) throw new MapFormatException(errors);
        var listing = Units.Listing(Units.Build(included), included.Types.Count);
        if (options.TryGetValue("out", out var outPath)) { File.WriteAllText(outPath, listing); Console.Error.WriteLine($"wrote {outPath}"); }
        else Console.Out.Write(listing);
        return 0;
    }

    private static async Task<int> CheckAsync(Dictionary<string, string> options)
    {
        var (map, text, root, mapDir) = LoadMap(options);
        var analysis = await AnalyzeAsync(root, map.Solution);
        var facts = Checker.Run(map, analysis, new CheckOptions { Commit = DetectCommit(root), MapText = text, LayoutText = ReadLayout(mapDir) });
        if (options.TryGetValue("facts", out var factsPath)) File.WriteAllText(factsPath, FactsJson.Serialize(facts));
        return Report(facts, options.ContainsKey("strict"));
    }

    /// <summary>Prints the warnings, the cycles per level and view, a summary line, how the test types were placed, and the patterns found. Returns the exit code.</summary>
    private static int Report(Facts facts, bool strict)
    {
        foreach (var w in facts.Warnings) Console.Error.WriteLine("warning: " + w);
        if (!facts.Passed)
        {
            foreach (var v in facts.Violations) Console.WriteLine($"[{v.Rule}] {v.Message}");
            Console.WriteLine($"{facts.Violations.Count} failure(s)");
            return 1;
        }
        foreach (var (level, m) in facts.Levels)
        {
            foreach (var cycle in m.Cycles) Console.WriteLine($"cycle at {level}: {string.Join(" → ", cycle)} → {cycle[0]}");
        }
        foreach (var (view, m) in facts.Views)
        {
            foreach (var cycle in m.Cycles) Console.WriteLine($"cycle in view {view}: {string.Join(" → ", cycle)} → {cycle[0]}");
        }
        var unsorted = facts.Types.Count(t => t.Leaf == ArchitectureMap.Unsorted);
        var leaves = facts.Map.Nodes.Count(kv => kv.Value.IsLeaf);
        var excluded = facts.Excluded.Types > 0 ? $", {facts.Excluded.Types} excluded" : "";
        var unsortedText = unsorted > 0 ? $", {unsorted} unsorted" : "";
        Console.WriteLine($"ok: {facts.Types.Count} types in {leaves} leaves{unsortedText}{excluded}; {facts.Dependencies.Count} dependencies, {facts.LeafEdges.Count} leaf edges, {facts.Warnings.Count} warning(s)");
        if (facts.Tests.Types.Count > 0) Console.WriteLine("tests: " + TestTypes.Placement(facts.Tests));
        foreach (var line in PatternDiscovery.Report(facts.Patterns)) Console.WriteLine(line);
        if (strict && facts.Warnings.Count > 0) { Console.WriteLine("strict: warnings are failures"); return 1; }
        return 0;
    }

    private static async Task<int> WhyAsync(List<string> positional, Dictionary<string, string> options)
    {
        if (positional.Count != 2) throw new ArgumentException("why requires two identifiers");
        Facts facts;
        if (options.TryGetValue("facts", out var factsPath))
        {
            facts = FactsJson.Deserialize<Facts>(File.ReadAllText(factsPath));
        }
        else
        {
            var (map, text, root, _) = LoadMap(options);
            var analysis = await AnalyzeAsync(root, map.Solution);
            facts = Checker.Run(map, analysis, new CheckOptions { MapText = text });
        }
        var (from, to) = (positional[0], positional[1]);
        foreach (var id in new[] { from, to })
        {
            if (!facts.Map.Exists(id) && id != ArchitectureMap.Unsorted) throw new ArgumentException($"\"{id}\" is not a node or external of the map");
        }
        var limit = int.Parse(options.GetValueOrDefault("limit", "20"));
        var pairs = Explain.Pairs(facts, from, to);
        var total = pairs.Sum(p => p.Count);
        Console.WriteLine($"{from} → {to}: {total} reference(s) across {pairs.Count} type pair(s)");
        foreach (var (a, b, n) in pairs.Take(limit)) Console.WriteLine($"{n,6}  {a} → {b}");
        return 0;
    }

    private static int Fmt(Dictionary<string, string> options)
    {
        var (map, text, _, mapDir) = LoadMap(options);
        var canonical = MapWriter.Write(map);
        var layoutPath = Path.Combine(mapDir, LayoutFile.FileName);
        string? layoutText = null, layoutCanonical = null;
        if (File.Exists(layoutPath))
        {
            layoutText = File.ReadAllText(layoutPath);
            var errors = new List<string>();
            var layout = LayoutFile.Read(layoutText, errors) ?? throw new MapFormatException(errors);
            if (errors.Count > 0) throw new MapFormatException(errors);
            layoutCanonical = layout.Write();
        }
        var changed = canonical != text || (layoutText is not null && layoutCanonical != layoutText);
        if (options.ContainsKey("check"))
        {
            Console.WriteLine(changed ? "not canonical" : "canonical");
            return changed ? 1 : 0;
        }
        if (canonical != text) File.WriteAllText(MapPath(options), canonical);
        if (layoutText is not null && layoutCanonical != layoutText) File.WriteAllText(layoutPath, layoutCanonical);
        Console.WriteLine(changed ? "formatted" : "already canonical");
        return 0;
    }

    private static async Task<int> FactsAsync(Dictionary<string, string> options)
    {
        var (map, text, root, mapDir) = LoadMap(options);
        var analysis = await AnalyzeAsync(root, map.Solution);
        var commit = options.GetValueOrDefault("commit") ?? DetectCommit(root);
        var facts = Checker.Run(map, analysis, new CheckOptions { Commit = commit, MapText = text, LayoutText = ReadLayout(mapDir) });
        var json = FactsJson.Serialize(facts);
        if (options.TryGetValue("out", out var outPath)) { File.WriteAllText(outPath, json); Console.Error.WriteLine($"wrote {outPath}"); }
        else Console.Out.Write(json);
        return facts.Passed ? 0 : 1;
    }

    private static int Diff(List<string> positional, Dictionary<string, string> options)
    {
        if (positional.Count != 2) throw new ArgumentException("diff requires a base and a head facts document");
        var baseFacts = FactsJson.Deserialize<Facts>(File.ReadAllText(positional[0]));
        var headFacts = FactsJson.Deserialize<Facts>(File.ReadAllText(positional[1]));
        var changeset = ChangesetBuilder.Build(baseFacts, headFacts);
        if (options.TryGetValue("repo", out var repo))
        {
            var baseRev = options.GetValueOrDefault("base-rev") ?? baseFacts.Commit ?? throw new ArgumentException("--base-rev is required when the base facts carry no commit");
            var headRev = options.GetValueOrDefault("head-rev") ?? headFacts.Commit ?? throw new ArgumentException("--head-rev is required when the head facts carry no commit");
            changeset = ChangesetBuilder.Build(baseFacts, headFacts, repo, baseRev.Replace("+dirty", ""), headRev.Replace("+dirty", ""));
        }
        WriteChangeset(changeset, options);
        return 0;
    }

    private static void WriteChangeset(Changeset changeset, Dictionary<string, string> options)
    {
        var json = FactsJson.Serialize(changeset);
        if (options.TryGetValue("out", out var outPath)) { File.WriteAllText(outPath, json); Console.Error.WriteLine($"wrote {outPath}"); }
        else Console.Out.Write(json);
    }

    /// <summary>
    /// Produces the changeset between two revisions in one step, by checking each out into a temporary worktree,
    /// building and analyzing both in parallel, and applying the current map to both. Ctrl+C stops the run and still
    /// removes the worktrees.
    /// </summary>
    private static async Task<int> ChangesetAsync(List<string> positional, Dictionary<string, string> options)
    {
        if (positional.Count is < 1 or > 2) throw new ArgumentException("changeset requires a base and an optional head, or one range");
        var (map, text, root, mapDir) = LoadMap(options);
        var (baseCommit, headCommit) = RevisionChangeset.ResolveRange(root, positional[0], positional.ElementAtOrDefault(1));
        Console.Error.WriteLine($"comparing {baseCommit[..8]} with {headCommit[..8]}");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            RoslynAnalyzer.EnsureMSBuild();
            var progress = new Progress(m => Console.Error.WriteLine("  " + m));
            var sw = Stopwatch.StartNew();
            var request = new ChangesetRequest(root, map, text, ReadLayout(mapDir), baseCommit, headCommit, Build: !options.ContainsKey("no-build"));
            var cache = options.ContainsKey("no-cache") ? null : ReviewCache.ForRepository(root);
            var result = await RevisionChangeset.BuildAsync(request, RoslynAnalyzer.AnalyzeAsync, progress, cancel.Token, cache);
            foreach (var (name, facts) in new[] { ("base", result.Base), ("head", result.Head) })
            {
                var unsorted = facts.Types.Count(t => t.Leaf == ArchitectureMap.Unsorted);
                Console.Error.WriteLine($"  {name}: {facts.Types.Count} types, {unsorted} unsorted, {facts.Warnings.Count} warning(s)");
            }
            var cs = result.Changeset;
            Console.Error.WriteLine($"  {cs.Files.Count(f => f.Status != "unchanged")} files and {cs.Edges.Count(e => e.Status != "unchanged")} edges changed, in {sw.Elapsed.TotalSeconds:F0}s");
            WriteChangeset(cs, options);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// Returns the number of a pull request given as a number, such as <c>42</c> or <c>#42</c>, or as its address on
    /// GitHub, which must name the repository that <c>gh</c> names.
    /// </summary>
    internal static int PullNumber(string given, IGitHub? github)
    {
        if (int.TryParse(given.TrimStart('#'), out var number) && number > 0) return number;
        var match = System.Text.RegularExpressions.Regex.Match(given, @"^https?://([^/]+)/([^/]+)/([^/]+)/pull/(\d+)");
        if (!match.Success) throw new ArgumentException($"\"{given}\" is neither a pull request number nor its address");
        if (github is not null && !$"{match.Groups[1].Value}/{match.Groups[2].Value}/{match.Groups[3].Value}".Equals($"{github.Repository.Host}/{github.Repository.NameWithOwner}", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{given} is not a pull request of {github.Repository.NameWithOwner}, the repository here");
        return int.Parse(match.Groups[4].Value);
    }

    /// <summary>
    /// Serves the review workflow and opens it in the browser: on the range given, on the pull request given when
    /// <paramref name="pull"/> is set, or on the picker when there is neither. Runs until Ctrl+C, and then cancels the
    /// reviews still running and removes their worktrees.
    /// </summary>
    private static async Task<int> ReviewAsync(List<string> positional, Dictionary<string, string> options, bool pull = false)
    {
        if (positional.Count > 2) throw new ArgumentException("review takes a base and an optional head, or one range");
        if (pull && positional.Count != 1) throw new ArgumentException("pr takes the number or the address of one pull request");
        var dir = Directory.GetCurrentDirectory();
        if (Git.TryRun(dir, "rev-parse", "--show-toplevel") is null) throw new ArgumentException("review must run inside a git repository");
        RoslynAnalyzer.EnsureMSBuild();
        // Without a map, a review asks Claude Code for one. A missing command line only costs the concept map.
        var claude = options.ContainsKey("no-ai") ? null : new ClaudeCli(log: m => Console.Error.WriteLine("  " + m));
        if (claude is not null && await claude.VersionAsync() is null)
        {
            claude = null;
            Console.Error.WriteLine("claude was not found on the path; reviews without a map draw the namespace skeleton");
        }
        var (github, githubNotice) = options.ContainsKey("no-github") ? (null, null) : await GitHubAccess.ConnectAsync(dir, TimeSpan.FromSeconds(10));
        if (githubNotice is not null) Console.Error.WriteLine(githubNotice);
        var serverOptions = new ReviewServerOptions
        {
            Directory = dir,
            Analyze = RoslynAnalyzer.AnalyzeAsync,
            Cache = options.ContainsKey("no-cache") ? null : ReviewCache.ForRepository(dir),
            MapPath = options.GetValueOrDefault("map"),
            Root = options.GetValueOrDefault("root"),
            Solution = options.GetValueOrDefault("solution"),
            Build = !options.ContainsKey("no-build"),
            ViewerDirectory = options.TryGetValue("viewer", out var viewer) ? Path.GetFullPath(viewer) : null,
            Port = int.Parse(options.GetValueOrDefault("port", "0")),
            Log = Console.Error.WriteLine,
            Claude = claude,
            Models = options.TryGetValue("model", out var model) ? ClaudeModels.All(model) : ClaudeModels.Default,
            GitHub = github,
            GitHubNotice = githubNotice,
            ClaudeNotice = options.ContainsKey("no-ai") ? null
                : "Claude Code was not found on the path, so the namespace skeleton is drawn. Install Claude Code and sign in, and vibecheck generates a concept map.",
        };
        await using var server = new ReviewServer(serverOptions);
        var url = await server.StartAsync();
        if (pull)
        {
            url = server.Link(await server.BeginPullAsync(PullNumber(positional[0], github)));
        }
        else if (positional.Count > 0)
        {
            var range = positional.Count == 2 ? $"{positional[0]}..{positional[1]}" : positional[0];
            var page = server.Begin(range);
            url = server.Link(page);
        }
        Console.WriteLine($"vibecheck is serving {url}");
        Console.WriteLine("press Ctrl+C to stop");
        if (!options.ContainsKey("no-open")) OpenBrowser(url);
        await server.WaitForShutdownAsync();
        Console.Error.WriteLine("stopping");
        return 0;
    }

    /// <summary>Opens a URL in the default browser, and says so when it cannot.</summary>
    private static void OpenBrowser(string url)
    {
        try
        {
            var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url) { UseShellExecute = false };
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("could not open a browser; open the link above");
        }
    }

    /// <summary>Reports progress on the calling thread as it arrives, unlike <see cref="Progress{T}"/>, which posts it.</summary>
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static string? ReadLayout(string mapDir)
    {
        var path = Path.Combine(mapDir, LayoutFile.FileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Returns the HEAD commit of the repository that contains a directory, suffixed with <c>+dirty</c> when the tree has changes, or <see langword="null"/> outside git.</summary>
    private static string? DetectCommit(string root)
    {
        var sha = Git.TryRun(root, "rev-parse", "HEAD");
        if (sha is null) return null;
        var status = Git.TryRun(root, "status", "--porcelain");
        return string.IsNullOrWhiteSpace(status) ? sha : sha + "+dirty";
    }
}
