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
