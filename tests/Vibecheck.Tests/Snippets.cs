using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Model;

namespace Vibecheck.Tests;

/// <summary>
/// Analyzes source snippets compiled in memory, as the Roslyn analysis would analyze a solution, and diffs two
/// versions of them into hunks, so that tests of declarations and clustering need neither MSBuild nor git.
/// </summary>
internal static class Snippets
{
    /// <summary>The directory the snippets' paths are relative to. Nothing is written there.</summary>
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vibecheck-snippets");

    private static readonly MetadataReference[] References = ReferencesOf("System.Private.CoreLib", "System.Runtime", "System.Collections");

    /// <summary>Returns the analysis of one project made of the files given, keyed by path relative to the root.</summary>
    public static AnalysisResult Analyze(IReadOnlyDictionary<string, string> files)
    {
        var resolver = new SourceResolver(Root);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Value, CSharpParseOptions.Default, Path.Combine(Root, f.Key))).ToList();
        var compilation = CSharpCompilation.Create("Snippets", trees, References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var located = trees.Select(t => (t, resolver.AddDocument(t.FilePath)!, (string?)null)).ToList();
        return RoslynAnalyzer.Collect(resolver, [new RoslynAnalyzer.ProjectTrees("Snippets", "", compilation, located)], []);
    }

    /// <summary>Returns the facts of the files given under a map that binds every type to one leaf.</summary>
    public static Facts Facts(IReadOnlyDictionary<string, string> files) => Checker.Run(Map(), Analyze(files));

    /// <summary>Returns a map with one leaf per top-level namespace segment the tests use, so that tests can count blocks.</summary>
    public static ArchitectureMap Map()
    {
        var map = new ArchitectureMap { Version = 0, Solution = "Snippets.sln" };
        foreach (var ns in new[] { "A", "B", "C" }) map.Nodes[ns.ToLowerInvariant()] = new NodeSpec { Name = ns, Bind = [ns + ".**"] };
        return map;
    }

    /// <summary>Returns the changeset between two versions of a set of files, with hunks that a line diff of each changed file gives.</summary>
    public static Changeset Changeset(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var hunks = new List<FileHunks>();
        var changes = new List<GitChange>();
        foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var a = before.GetValueOrDefault(path);
            var b = after.GetValueOrDefault(path);
            if (a == b) continue;
            changes.Add(new GitChange(path, a is null ? "added" : b is null ? "removed" : "modified", null, a is null ? null : "x", b is null ? null : "y"));
            hunks.Add(new FileHunks(path, Diff(a ?? "", b ?? "")));
        }
        return ChangesetBuilder.Build(Facts(before), Facts(after), hunks, changes);
    }

    /// <summary>Returns the hunks of a line diff of two texts, with three lines of context as git gives them.</summary>
    public static List<Hunk> Diff(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<(char Op, string Text)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { ops.Add((' ', a[x])); x++; y++; }
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1])) ops.Add(('-', a[x++]));
            else ops.Add(('+', b[y++]));
        }

        var changed = ops.Select((o, i) => (o, i)).Where(p => p.o.Op != ' ').Select(p => p.i).ToList();
        var hunks = new List<Hunk>();
        var k = 0;
        while (k < changed.Count)
        {
            var start = Math.Max(0, changed[k] - 3);
            var end = changed[k];
            while (k + 1 < changed.Count && changed[k + 1] - end <= 7) end = changed[++k];
            k++;
            end = Math.Min(ops.Count - 1, end + 3);
            int baseBefore = ops.Take(start).Count(o => o.Op != '+'), headBefore = ops.Take(start).Count(o => o.Op != '-');
            var range = ops.Skip(start).Take(end - start + 1).ToList();
            int baseCount = range.Count(o => o.Op != '+'), headCount = range.Count(o => o.Op != '-');
            var header = $"@@ -{(baseCount == 0 ? baseBefore : baseBefore + 1)},{baseCount} +{(headCount == 0 ? headBefore : headBefore + 1)},{headCount} @@";
            hunks.Add(new Hunk(header, range.Select(o => new[] { o.Op.ToString(), o.Text }).ToList()));
        }
        return hunks;
    }

    private static string[] Lines(string text) => text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n');

    private static MetadataReference[] ReferencesOf(params string[] names)
    {
        var dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return names.Select(n => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(dir, n + ".dll"))).ToArray();
    }
}
