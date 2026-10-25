using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Model;

namespace Vibecheck.Tests;

/// <summary>
/// Analyzes source snippets compiled in memory, as the Roslyn analysis would analyze a solution, and diffs two
/// versions of them into hunks, so that tests of declarations and clustering need neither MSBuild nor git. Files under
/// <c>tests/</c> make a second project, <c>Snippets.Tests</c>, which references the first and the test frameworks that
/// <see cref="Frameworks"/> declares.
/// </summary>
internal static class Snippets
{
    /// <summary>The directory the snippets' paths are relative to. Nothing is written there.</summary>
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vibecheck-snippets");

    private static readonly MetadataReference[] References = ReferencesOf("System.Private.CoreLib", "System.Runtime", "System.Collections");

    /// <summary>The folder whose files make the test project.</summary>
    public const string TestFolder = "tests/";

    /// <summary>
    /// Gets stand-ins for the test frameworks, each compiled into an assembly named as the framework's is, so that the
    /// suite needs no framework it does not already use. Each assembly declares the attributes and assertions the tests
    /// name, and <c>Xunit.SkippableFact</c> declares an attribute that derives from xUnit's <c>Fact</c>.
    /// </summary>
    private static readonly Lazy<MetadataReference[]> Frameworks = new(() =>
    {
        var core = Compile("xunit.core", """
            namespace Xunit
            {
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public class FactAttribute : System.Attribute { public string? Skip { get; set; } }
                public class TheoryAttribute : FactAttribute { }
                public sealed class InlineDataAttribute : System.Attribute { public InlineDataAttribute(params object[] data) { } }
            }
            """);
        var assert = Compile("xunit.assert", """
            namespace Xunit
            {
                public static class Assert
                {
                    public static void Equal<T>(T expected, T actual) { }
                    public static void True(bool condition) { }
                    public static void NotNull(object? value) { }
                    public static void All<T>(System.Collections.Generic.IEnumerable<T> items, System.Action<T> check) { }
                }
            }
            """);
        var nunit = Compile("nunit.framework", """
            namespace NUnit.Framework
            {
                public sealed class TestAttribute : System.Attribute { }
                public sealed class TestFixtureAttribute : System.Attribute { }
                public sealed class IgnoreAttribute : System.Attribute { public IgnoreAttribute(string reason) { } }
                public static class Assert { public static void That(object? actual, object constraint) { } }
                public static class Is { public static object EqualTo(object? expected) => new object(); }
            }
            """);
        var skippable = Compile("Xunit.SkippableFact", """
            namespace Xunit { public sealed class SkippableFactAttribute : FactAttribute { } }
            """, core);
        return [core, assert, nunit, skippable];
    });

    /// <summary>
    /// Returns the analysis of the files given, keyed by path relative to the root: one project of the files outside
    /// <see cref="TestFolder"/>, and a test project of those inside it when there are any. Files that are not C# take no
    /// part in the analysis, as a document in a solution's folder does not. Both projects reference the libraries
    /// given, which <see cref="Library"/> compiles.
    /// </summary>
    public static AnalysisResult Analyze(IReadOnlyDictionary<string, string> files, params MetadataReference[] libraries)
    {
        var resolver = new SourceResolver(Root);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        List<SyntaxTree> Parse(bool tests) => files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal) && f.Key.StartsWith(TestFolder, StringComparison.Ordinal) == tests)
            .Select(f => CSharpSyntaxTree.ParseText(f.Value, CSharpParseOptions.Default, Path.Combine(Root, f.Key))).ToList();
        RoslynAnalyzer.ProjectTrees Project(string name, Compilation compilation) =>
            new(name, "", compilation, compilation.SyntaxTrees.Select(t => (t, resolver.AddDocument(t.FilePath)!, (string?)null)).ToList());

        var compilation = CSharpCompilation.Create("Snippets", Parse(tests: false), [.. References, .. libraries], options);
        var projects = new List<RoslynAnalyzer.ProjectTrees> { Project("Snippets", compilation) };
        var testTrees = Parse(tests: true);
        if (testTrees.Count > 0)
            projects.Add(Project("Snippets.Tests", CSharpCompilation.Create("Snippets.Tests", testTrees, [.. References, .. libraries, .. Frameworks.Value, compilation.ToMetadataReference()], options)));
        return RoslynAnalyzer.Collect(resolver, projects, []);
    }

    /// <summary>Returns the facts of the files given under a map that binds every type to one leaf.</summary>
    public static Facts Facts(IReadOnlyDictionary<string, string> files) => Checker.Run(Map(), Analyze(files));

    /// <summary>
    /// Returns a library compiled from a source text into an assembly of the given name, such as a stand-in for a
    /// framework whose types the snippets use, so that the suite needs no framework it does not already use.
    /// </summary>
    public static MetadataReference Library(string assembly, string source) => Compile(assembly, source);

    /// <summary>
    /// Returns a map with one leaf per top-level namespace segment the tests use, so that tests can count blocks, and
    /// with the test project's types as its test types.
    /// </summary>
    public static ArchitectureMap Map()
    {
        var map = new ArchitectureMap { Version = 0, Solution = "Snippets.sln", Tests = ["Snippets.Tests::**"] };
        foreach (var ns in new[] { "A", "B", "C" }) map.Nodes[ns.ToLowerInvariant()] = new NodeSpec { Name = ns, Bind = [ns + ".**"] };
        return map;
    }

    /// <summary>Returns the changeset between two versions of a set of files, with hunks that a line diff of each changed file gives.</summary>
    public static Changeset Changeset(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var hunks = new List<FileHunks>();
        var changes = new List<GitChange>();
        var texts = new Dictionary<string, (string? Base, string? Head)>(StringComparer.Ordinal);
        foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var a = before.GetValueOrDefault(path);
            var b = after.GetValueOrDefault(path);
            if (a == b) continue;
            changes.Add(new GitChange(path, a is null ? "added" : b is null ? "removed" : "modified", null, a is null ? null : "x", b is null ? null : "y"));
            hunks.Add(new FileHunks(path, Diff(a ?? "", b ?? "")));
            texts[path] = (a, b);
        }
        return ChangesetBuilder.Build(Facts(before), Facts(after), hunks, changes, texts, paths => paths.Where(after.ContainsKey).ToDictionary(p => p, p => after[p]));
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

    /// <summary>Compiles a source text into an assembly of the given name, in memory, and returns a reference to its image.</summary>
    private static MetadataReference Compile(string assembly, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText(source)], [.. References, .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success) throw new InvalidOperationException($"{assembly} does not compile: {string.Join("; ", emitted.Diagnostics)}");
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static MetadataReference[] ReferencesOf(params string[] names)
    {
        var dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return names.Select(n => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(dir, n + ".dll"))).ToArray();
    }
}
