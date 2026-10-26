using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vibecheck.Analysis;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the dependencies the analysis records from source snippets. See <c>docs/vibecheck-spec.md</c> section 3.</summary>
public class DependencyWalkerTests
{
    [Fact]
    public void ABaseListAndAConstraintOfATypeAreItsDependencies()
    {
        var analysis = Snippets.Analyze(new Dictionary<string, string>
        {
            ["A/Kinds.cs"] = """
                namespace A
                {
                    public class Base { }
                    public interface IPort { }
                    public sealed class MarkAttribute : System.Attribute { }
                    public interface IBox<T> where T : IPort { }
                }
                """,
            ["B/Thing.cs"] = """
                namespace B
                {
                    [A.Mark]
                    public sealed class Thing<T> : A.Base, A.IPort where T : A.IPort { }
                }
                """,
        });
        var kinds = analysis.Dependencies.Where(d => d.FromType == "B.Thing").Select(d => $"{d.Kind} {d.ToType}").Order(StringComparer.Ordinal);
        Assert.Equal(["attribute A.MarkAttribute", "implements A.IPort", "inherits A.Base", "references A.IPort"], kinds);
        // A constraint belongs to the type that declares it.
        Assert.Contains(analysis.Dependencies, d => d.FromType == "A.IBox" && d.ToType == "A.IPort");
    }

    /// <summary>The library a table refers to: an enum, a row type, a factory, a generic holder and an alias target.</summary>
    private const string TableLibrary = """
        namespace A
        {
            public enum Kind { X, Y }
            public sealed class Row { public Row(Kind kind, string text, int size) { } }
            public static class Helper { public static int Make(int n) => n; }
            public sealed class Rows<T> { public static readonly Rows<T> Empty = new(); public Rows(params T[] items) { } }
            public sealed class Thing { public int Count => 0; public static Thing Create() => new(); }
        }
        """;

    [Fact]
    public void QualifiedNamesInAnInitializerBindToTheirTypes()
    {
        var analysis = Snippets.Analyze(new Dictionary<string, string>
        {
            ["A/Lib.cs"] = TableLibrary,
            ["B/Table.cs"] = """
                namespace B
                {
                    public static class Table
                    {
                        public static global::A.Rows<global::A.Row> Rows { get; } = new global::A.Rows<global::A.Row>(
                            new global::A.Row(global::A.Kind.X, "x", global::A.Helper.Make(1)),
                            new global::A.Row(global::A.Kind.Y, "y", global::A.Helper.Make(2)));
                        public static global::A.Rows<global::A.Kind> Kinds = global::A.Rows<global::A.Kind>.Empty;
                    }
                }
                """,
        });
        var edges = analysis.Dependencies.Where(d => d.FromType == "B.Table").Select(d => $"{d.Line} {d.Kind} {d.Symbol}").Order(StringComparer.Ordinal);
        // Each qualified type name is a reference of its own, beside the member it qualifies, and a namespace is none.
        Assert.Equal(
        [
            "5 constructs A.Row", "5 constructs A.Rows<T>", "5 references A.Row", "5 references A.Rows<T>",
            "6 calls A.Helper.Make", "6 constructs A.Row", "6 references A.Helper", "6 references A.Kind", "6 references A.Kind.X",
            "7 calls A.Helper.Make", "7 constructs A.Row", "7 references A.Helper", "7 references A.Kind", "7 references A.Kind.Y",
            "8 references A.Kind", "8 references A.Kind", "8 references A.Rows<T>", "8 references A.Rows<T>", "8 references A.Rows<T>.Empty",
        ], edges);
    }

    [Fact]
    public void AnAliasACallThroughADelegateAndAShadowingParameterBindAsWritten()
    {
        var analysis = Snippets.Analyze(new Dictionary<string, string>
        {
            ["A/Lib.cs"] = TableLibrary,
            ["B/User.cs"] = """
                using Z = A.Helper;
                namespace B
                {
                    public sealed class User
                    {
                        private System.Action go = () => { };
                        public int Alias() => Z.Make(1);
                        public void Delegate() => go();
                        public int Shadowed(A.Thing Thing) => Thing.Count + A.Thing.Create().Count;
                    }
                }
                """,
        });
        var edges = analysis.Dependencies.Where(d => d.FromType == "B.User").Select(d => $"{d.Line} {d.Kind} {d.Symbol}").Order(StringComparer.Ordinal).ToList();
        // The parameter named Thing shadows the type, so Thing.Count binds the parameter and records only the member.
        Assert.Equal(["7 calls A.Helper.Make", "9 calls A.Thing.Create", "9 references A.Thing", "9 references A.Thing", "9 references A.Thing.Count", "9 references A.Thing.Count"], edges);
        // The call through the delegate names the field, which is a site of the type's own.
        Assert.Contains(analysis.Sites, s => s.FromType == "B.User" && s.Symbol == "B.User.go" && s.Line == 8);
    }

    [Fact]
    public void ALargeQualifiedTableWalksInLinearTime()
    {
        // A generated table whose every name is global-qualified. Before names were bound through the name binder,
        // each qualifier bound the whole initializer again, and five hundred rows took seven seconds.
        var rows = string.Join(",\n", Enumerable.Range(0, 500).Select(i =>
            $"            new global::A.Row(global::A.Kind.X, \"{i}\", global::A.Helper.Make({i}))"));
        var table = $$"""
            namespace B
            {
                public static class Table
                {
                    public static global::A.Rows<global::A.Row> Rows { get; } = new global::A.Rows<global::A.Row>(
            {{rows}});
                }
            }
            """;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var analysis = Snippets.Analyze(new Dictionary<string, string> { ["A/Lib.cs"] = TableLibrary, ["B/Table.cs"] = table });
        // The rows start on line 6; line 5 constructs the holder, whose type argument also counts as constructed.
        Assert.Equal(500, analysis.Dependencies.Count(d => d.FromType == "B.Table" && d.Kind == "constructs" && d.ToType == "A.Row" && d.Line >= 6));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the walk took {watch.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void AFileCompiledIntoTwoProjectsIsWalkedOnceWithTheFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibecheck-linked");
        var resolver = new SourceResolver(root);
        var core = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        SyntaxTree Tree(string path, string text) => CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default, Path.Combine(root, path));
        RoslynAnalyzer.ProjectTrees Project(string name, params SyntaxTree[] trees) => new(name, name, CSharpCompilation.Create(name, trees, [core],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)), trees.Select(t => (t, resolver.AddDocument(t.FilePath)!, (string?)null)).ToList());

        const string shared = "namespace S { public class Clock { public static int Now() => Tick(); static int Tick() => 0; } }";
        var second = Project("Second", Tree("Shared/Clock.cs", shared), Tree("B/User.cs", "namespace B { class User { int t = S.Clock.Now(); } }"));
        var first = Project("First", Tree("Shared/Clock.cs", shared), Tree("A/User.cs", "namespace A { class User { int t = S.Clock.Now(); } }"));
        var analysis = RoslynAnalyzer.Collect(resolver, [second, first], []);

        var file = Assert.Single(analysis.Files, f => f.Path == "Shared/Clock.cs");
        Assert.Equal("Second", file.Project);
        Assert.Single(analysis.Declarations, d => d.File == "Shared/Clock.cs" && d.Kind == "type");
        Assert.Single(analysis.Sites, s => s.File == "Shared/Clock.cs" && s.Symbol == "S.Clock.Tick");
        // Each project's own file still reaches the clock its own assembly compiles.
        Assert.Equal(["A/User.cs First::S.Clock", "B/User.cs Second::S.Clock"], analysis.Dependencies.Select(d => $"{d.File} {d.ToType}").Distinct());
    }
}
