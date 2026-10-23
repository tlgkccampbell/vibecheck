using System.Diagnostics;
using Archmap.Analysis;
using Archmap.Bootstrap;
using Archmap.Checking;
using Archmap.Diffing;
using Archmap.Format;
using Archmap.Model;

namespace Archmap.Cli;

/// <summary>The <c>archmap</c> command-line tool. See <c>docs/archmap-spec.md</c> section 7.</summary>
public static class Program
{
    private const string Usage = """
        usage:
          archmap init  --solution <path.sln> [--root <dir>] [--map <archmap.json>] [--facts <out.json>]
          archmap check [--map <archmap.json>] [--propose <out.json>] [--facts <out.json>]
          archmap fmt   [--map <archmap.json>] [--check]
          archmap facts [--map <archmap.json>] [--out <out.json>] [--commit <sha>]
          archmap diff  <base-facts.json> <head-facts.json> [--out <out.json>] [--repo <dir> --base-rev <rev> --head-rev <rev>]

        The map defaults to ./archmap.json. The repository root is the map's directory.
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
            var (positional, options) = ParseArgs(args.Skip(1));
            return args[0] switch
            {
                "init" => await InitAsync(options),
                "check" => await CheckAsync(options),
                "fmt" => Fmt(options),
                "facts" => await FactsAsync(options),
                "diff" => Diff(positional, options),
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
        Path.GetFullPath(options.GetValueOrDefault("map", "archmap.json"));

    private static (ArchMap Map, string Text, string Root) LoadMap(Dictionary<string, string> options)
    {
        var path = MapPath(options);
        if (!File.Exists(path)) throw new IOException($"map not found: {path}");
        var text = File.ReadAllText(path);
        return (MapReader.Read(text), text, Path.GetDirectoryName(path)!);
    }

    private static async Task<AnalysisResult> AnalyzeAsync(string root, string solution)
    {
        var solutionPath = Path.GetFullPath(Path.Combine(root, solution));
        if (!File.Exists(solutionPath)) throw new IOException($"solution not found: {solutionPath}");
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
        var mapPath = Path.GetFullPath(options.GetValueOrDefault("map", Path.Combine(root, "archmap.json")));
        if (File.Exists(mapPath)) throw new IOException($"{mapPath} already exists; init does not overwrite a map");

        var analysis = await AnalyzeAsync(root, solutionPath);
        var map = Skeleton.Build(analysis, Path.GetRelativePath(root, solutionPath));
        File.WriteAllText(mapPath, MapWriter.Write(map));
        Console.WriteLine($"wrote {mapPath}: {map.Nodes.Count} nodes, every level observed");

        var facts = Checker.Run(map, analysis, new CheckOptions { Commit = DetectCommit(root) });
        var factsPath = options.GetValueOrDefault("facts", Path.Combine(root, "archmap.facts.json"));
        File.WriteAllText(factsPath, FactsJson.Serialize(facts));
        Console.WriteLine($"wrote {factsPath}");
        return Report(facts);
    }

    private static async Task<int> CheckAsync(Dictionary<string, string> options)
    {
        var (map, text, root) = LoadMap(options);
        var analysis = await AnalyzeAsync(root, map.Solution);
        var facts = Checker.Run(map, analysis, new CheckOptions { Commit = DetectCommit(root), MapText = text, LayoutText = ReadLayout(root) });
        if (options.TryGetValue("facts", out var factsPath)) File.WriteAllText(factsPath, FactsJson.Serialize(facts));
        if (options.TryGetValue("propose", out var proposePath))
        {
            var proposals = facts.Violations.Where(v => v.Proposal is not null).Select(v => v.Proposal!).Distinct().ToList();
            File.WriteAllText(proposePath, MapWriter.Write(Proposals.Apply(map, proposals)));
            Console.WriteLine($"wrote {proposePath} with {proposals.Count} proposed edit(s)");
        }
        return Report(facts);
    }

    private static int Report(Facts facts)
    {
        foreach (var w in facts.Warnings) Console.Error.WriteLine("warning: " + w);
        if (facts.Passed)
        {
            Console.WriteLine($"ok: {facts.Types.Count} types in {facts.Files.Count} files, {facts.Dependencies.Count} dependencies, {facts.Map.Edges.Count} declared edges");
            return 0;
        }
        foreach (var v in facts.Violations)
        {
            Console.WriteLine($"[{v.Rule}] {v.Message}");
            if (v.Proposal is { } p) Console.WriteLine($"    proposal: {DescribeProposal(p)}");
        }
        Console.WriteLine($"{facts.Violations.Count} violation(s)");
        return 1;
    }

    private static string DescribeProposal(ProposalFact p) => p.Op switch
    {
        "add-edge" => $"declare edge {p.From} → {p.To}",
        "remove-edge" => $"remove edge {p.From} → {p.To}",
        "add-leaf" => $"add leaf {p.Node} bound to {string.Join(", ", p.Bind ?? [])}",
        _ => p.Op,
    };

    private static int Fmt(Dictionary<string, string> options)
    {
        var (map, text, root) = LoadMap(options);
        var canonical = MapWriter.Write(map);
        var layoutPath = Path.Combine(root, LayoutFile.FileName);
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
        var (map, text, root) = LoadMap(options);
        var analysis = await AnalyzeAsync(root, map.Solution);
        var commit = options.GetValueOrDefault("commit") ?? DetectCommit(root);
        var facts = Checker.Run(map, analysis, new CheckOptions { Commit = commit, MapText = text, LayoutText = ReadLayout(root) });
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
        List<FileHunks>? hunks = null;
        if (options.TryGetValue("repo", out var repo))
        {
            var baseRev = options.GetValueOrDefault("base-rev") ?? baseFacts.Commit ?? throw new ArgumentException("--base-rev is required when the base facts carry no commit");
            var headRev = options.GetValueOrDefault("head-rev") ?? headFacts.Commit ?? throw new ArgumentException("--head-rev is required when the head facts carry no commit");
            var preview = ChangesetBuilder.Build(baseFacts, headFacts);
            var changed = preview.Files.Where(f => f.Status != "unchanged").Select(f => f.Path);
            hunks = GitHunks.Collect(repo, baseRev.Replace("+dirty", ""), headRev.Replace("+dirty", ""), changed);
        }
        var changeset = ChangesetBuilder.Build(baseFacts, headFacts, hunks);
        var json = FactsJson.Serialize(changeset);
        if (options.TryGetValue("out", out var outPath)) { File.WriteAllText(outPath, json); Console.Error.WriteLine($"wrote {outPath}"); }
        else Console.Out.Write(json);
        var gratuitous = changeset.Structural.Nodes.Count(n => n.Gratuitous) + changeset.Structural.Edges.Count(e => e.Gratuitous);
        if (gratuitous > 0) Console.Error.WriteLine($"{gratuitous} gratuitous map edit(s)");
        return 0;
    }

    private static string? ReadLayout(string root)
    {
        var path = Path.Combine(root, LayoutFile.FileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Returns the HEAD commit of the repository that contains a directory, suffixed with <c>+dirty</c> when the tree has changes, or <see langword="null"/> outside git.</summary>
    private static string? DetectCommit(string root)
    {
        try
        {
            var sha = Git(root, "rev-parse", "HEAD");
            if (sha is null) return null;
            var status = Git(root, "status", "--porcelain");
            return string.IsNullOrWhiteSpace(status) ? sha : sha + "+dirty";
        }
        catch
        {
            return null;
        }
    }

    private static string? Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(root);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : null;
    }
}
