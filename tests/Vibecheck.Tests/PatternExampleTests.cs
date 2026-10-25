using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the examples of instances, their sources and the compare view's rows. See <c>docs/patterns.md</c> sections 6 and 7.3.</summary>
public class PatternExampleTests
{
    [Fact]
    public void TheNearestSharesTheMostFeaturesAndTheTypicalIsTheSameForEveryInstanceAndNeitherIsNew()
    {
        var types = new[] { T("A.X"), T("A.Y"), T("A.P"), T("A.Q"), T("A.R") };
        var features = new Dictionary<string, SortedSet<string>>
        {
            ["A.X"] = F("a", "b"), ["A.Y"] = F("c"),
            ["A.P"] = F("a", "b", "c"), ["A.Q"] = F("a"), ["A.R"] = F("c", "d"),
        };
        var chosen = Choose(types, added: ["A.X", "A.Y"], features);
        // P shares two of X's three features, and Y is new, so it is never an example though it has the same features.
        Assert.Equal(("A.P", "A.Q"), chosen["A.X"]);
        // P is the typical, sharing the most with the other candidates, unless it is the nearest, when the next one takes its place.
        Assert.Equal(("A.R", "A.P"), chosen["A.Y"]);
    }

    [Fact]
    public void ATieGoesToTheSameLeafThenToTheLongestSharedNamespaceOfTheKeyTypeArguments()
    {
        var types = new[] { T("A.X"), T("A.P", leaf: "b"), T("A.Q"), T("A.R") };
        var features = types.ToDictionary(t => t.Name, _ => F("a"));
        Assert.Equal("A.Q", Choose(types, ["A.X"], features)["A.X"].Nearest);

        var arguments = new Dictionary<string, List<string>>
        {
            ["A.X"] = ["Billing.Events.Paid"], ["A.P"] = ["Billing.Events.Refunded"], ["A.Q"] = ["Auth.Events.Login"], ["A.R"] = ["Billing.Orders.Placed"],
        };
        Assert.Equal("A.P", Choose(types, ["A.X"], features, arguments, leafOfP: "a")["A.X"].Nearest);
    }

    [Fact]
    public void TheCompareViewPairsTheConstructorThenByNameThenByShapeAndGivesTheRestRowsOfTheirOwn()
    {
        var files = new Dictionary<string, string>
        {
            ["A/Left.cs"] = """
                namespace A;
                public sealed class Left
                {
                    public Left(int a) { }
                    public void Run() { }
                    public int Count(string s) => 1;
                    public void OnlyLeft(long x, long y) { }
                }
                """,
            ["A/Right.cs"] = """
                namespace A;
                public sealed class Right
                {
                    public Right(int a, int b) { }
                    public int Measure(string s) => 2;
                    public void Run() { }
                    public string OnlyRight => "";
                }
                """,
        };
        var analysis = Snippets.Analyze(files);
        var left = PatternExamples.OutlineOf("A.Left", analysis.Declarations, files)!;
        var right = PatternExamples.OutlineOf("A.Right", analysis.Declarations, files)!;
        var rows = PatternExamples.Align(left, right);
        // A member on the right alone follows the row of the member before it on the right.
        Assert.Equal(["A.Left|A.Right", "Left|Right", "Run|Run", "|OnlyRight", "Count|Measure", "OnlyLeft|"], rows.Select(r => $"{r.Left}|{r.Right}"));
        Assert.Equal((new LineRange(2, 3), new LineRange(2, 3)), (rows[0].LeftLines, rows[0].RightLines));
        Assert.Equal(new LineRange(5, 5), rows[2].LeftLines);
        Assert.Equal(new LineRange(6, 6), rows[2].RightLines);
        Assert.Equal(("A/Left.cs", 2), (left.Source.File, left.Source.First));
        Assert.Equal("public sealed class Left", left.Source.Lines[0]);
        Assert.False(left.Source.Cut);
    }

    [Fact]
    public void ATypeLongerThanTheLimitIsCutAtItsLastWholeMember()
    {
        var members = string.Join("\n", Enumerable.Range(1, 80).Select(i => $"    public void M{i}()\n    {{\n        System.Console.WriteLine({i});\n    }}"));
        var files = new Dictionary<string, string> { ["A/Long.cs"] = $"namespace A;\npublic sealed class Long\n{{\n{members}\n}}\n" };
        var outline = PatternExamples.OutlineOf("A.Long", Snippets.Analyze(files).Declarations, files)!;
        Assert.True(outline.Source.Cut);
        Assert.True(outline.Source.Lines.Count <= PatternExamples.MostLines);
        Assert.Equal("    }", outline.Source.Lines[^1]);
        Assert.Equal(outline.Source.First + outline.Source.Lines.Count - 1, outline.Members[^1].LastLine);
    }

    [Fact]
    public void AChangeCarriesTheExamplesTheirSourcesAndTheRowsWithDeparturesMarked()
    {
        var before = new Dictionary<string, string>
        {
            ["A/Bus.cs"] = "namespace A { public interface IConsumer<T> { } public interface IUnitOfWork { } public sealed class AuditAttribute : System.Attribute { } }",
        };
        for (var i = 1; i <= 5; i++)
            before[$"A/C{i}.cs"] = $"namespace A\n{{\n    public sealed class C{i} : IConsumer<int>\n    {{\n        public C{i}(IUnitOfWork work) {{ }}\n        public void Consume() {{ }}\n    }}\n}}\n";
        var after = new Dictionary<string, string>(before)
        {
            ["A/C6.cs"] = "namespace A\n{\n    [Audit]\n    public sealed class C6 : IConsumer<int>\n    {\n        public C6() { }\n        public void Consume() { }\n    }\n}\n",
        };
        var p = Assert.Single(Snippets.Changeset(before, after).Patterns);
        var instance = Assert.Single(p.Instances);
        Assert.Equal(new ExampleChoice("A.C1", "A.C2"), instance.Examples);
        Assert.Equal(["A.C1", "A.C2", "A.C6"], p.Sources.Keys);
        Assert.Equal(["A.C1", "A.C2"], instance.Compare.Keys);
        var rows = instance.Compare["A.C1"];
        Assert.Equal(["A.C6|A.C1", "C6|C1", "Consume|Consume"], rows.Select(r => $"{r.Left}|{r.Right}"));
        Assert.Equal(["only this instance carries [Audit]"], rows[0].Marks!);
        Assert.Equal(["no IUnitOfWork"], rows[1].Marks!);
        Assert.Null(rows[2].Marks);
    }

    private static TypeFact T(string name, string leaf = "a") => new(name, "Snippets", leaf, "pattern", [$"src/{name}.cs"]);

    private static SortedSet<string> F(params string[] features) => new(features, StringComparer.Ordinal);

    private static Dictionary<string, (string? Nearest, string? Typical)> Choose(TypeFact[] types, string[] added, Dictionary<string, SortedSet<string>> features,
        Dictionary<string, List<string>>? arguments = null, string? leafOfP = null)
    {
        if (leafOfP is not null) types = types.Select(t => t.Name == "A.P" ? t with { Leaf = leafOfP } : t).ToArray();
        return PatternExamples.Choose(types.Where(t => added.Contains(t.Name)), types, added.ToHashSet(), features,
            arguments ?? types.ToDictionary(t => t.Name, _ => new List<string>()));
    }
}
