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
}
