using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the reading path's tiers and order. See <c>docs/attention.md</c> section 6.</summary>
public class ReadingPathTests
{
    /// <summary>The synthetic map with a category on each top-level node: Shared rests under the others, A is the domain, B is a feature.</summary>
    private static ArchitectureMap Map()
    {
        var map = TestData.Map();
        map.Nodes["a"] = new NodeSpec { Name = "A", Category = "domain" };
        map.Nodes["b"] = new NodeSpec { Name = "B", Bind = ["B.**"], Category = "feature" };
        map.Nodes["shared"] = new NodeSpec { Name = "Shared", Bind = ["Shared.**"], Category = "foundation" };
        return map;
    }

    private static AnalysisResult Changed(AnalysisResult analysis, params string[] paths) =>
        analysis with { Files = analysis.Files.Select(f => paths.Contains(f.Path) ? f with { Hash = "changed" } : f).ToList() };

    [Fact]
    public void SurprisesComeFirstInTheOrderOfTheirSignalsAndAreNotListedAgain()
    {
        var map = Map();
        // A does not use Shared here, so that a new reference out of Shared closes no cycle at the top level.
        static AnalysisResult WithoutResult(AnalysisResult a) => a with { Dependencies = a.Dependencies.Where(d => d.ToType != TestData.Result).ToList() };
        var baseFacts = Checker.Run(map, WithoutResult(TestData.Analysis()), new CheckOptions { Commit = "base" });
        // Three new references: B now uses A (the largest), Shared now uses A (out of a foundation block), and Y now
        // uses X, which closes a cycle inside A.
        var head = WithoutResult(TestData.Analysis(
        [
            new RawDependency(TestData.Service, TestData.Thing, "calls", "A.X.Thing.Go", TestData.B1, 7),
            new RawDependency(TestData.Service, TestData.Thing, "calls", "A.X.Thing.Stop", TestData.B1, 8),
            new RawDependency(TestData.Service, TestData.Helper, "references", "A.X.Helper", TestData.B1, 9),
            new RawDependency(TestData.Result, TestData.Thing, "references", "A.X.Thing", TestData.S1, 3),
            new RawDependency(TestData.Other, TestData.Thing, "calls", "A.X.Thing.Back", TestData.Y1, 2),
        ]));
        var cs = ChangesetBuilder.Build(baseFacts, Checker.Run(map, Changed(head, TestData.B1, TestData.S1, TestData.Y1), new CheckOptions { Commit = "head" }));

        var first = cs.Path.Where(s => s.Tier == "first").ToList();
        Assert.Equal(["edge", "edge", "cycle", "node"], first.Select(s => s.Kind));
        Assert.Equal(("shared→a", "root"), (first[0].Target, first[0].Location));
        Assert.Equal([TestData.S1], first[0].Files);
        Assert.Contains("a foundation block", first[0].Signal);
        Assert.Equal(("b→a", "root"), (first[1].Target, first[1].Location));
        Assert.Equal([TestData.B1], first[1].Files);
        Assert.Contains("3 references in 2 pairs", first[1].Signal);
        Assert.Equal(("a.y→a.x", "a"), (first[2].Target, first[2].Location));
        Assert.Equal(["a.x", "a.y"], first[2].Members!.OrderBy(m => m, StringComparer.Ordinal));
        Assert.Contains("closed by Y → X", first[2].Signal);
        // A is the changed block with the most references into it from its level, from B.
        Assert.Equal("a", first[3].Target);
        Assert.Contains("4 references into it from 2 blocks", first[3].Signal);
        // The new edge that closed the cycle is not listed twice, and every changed block was named by a surprise.
        Assert.DoesNotContain(cs.Path, s => s.Kind == "edge" && s.Target == "a.y→a.x");
        Assert.DoesNotContain(cs.Path, s => s.Tier != "first");
    }

    [Fact]
    public void TheWidestDecisionLeadsAndItsConsequencesAreSetAside()
    {
        const string clock = "namespace A;\n\npublic class Clock\n{\n    public int Now(int zone) => zone;\n}\n";
        const string caller = "namespace B;\n\npublic class Caller\n{\n    public int First(A.Clock clock) => clock.Now(1);\n}\n";
        const string other = "namespace C;\n\npublic class Other\n{\n    public int Value => 1;\n}\n";
        var before = new Dictionary<string, string> { ["src/Clock.cs"] = clock, ["src/Caller.cs"] = caller, ["src/Other.cs"] = other };
        var after = new Dictionary<string, string>
        {
            ["src/Clock.cs"] = clock.Replace("Now(int zone)", "Now(int zone, bool utc)"),
            ["src/Caller.cs"] = caller.Replace("Now(1)", "Now(1, true)"),
            ["src/Other.cs"] = other.Replace("=> 1", "=> 2"),
        };
        var cs = Snippets.Changeset(before, after);

        var decision = Assert.Single(cs.Path, s => s.Kind == "decision");
        Assert.Equal(("first", "src/Clock.cs#0"), (decision.Tier, decision.Target));
        Assert.Equal(["src/Clock.cs"], decision.Files);
        Assert.Contains("signature of Clock.Now changed, public: 1 consequence in 1 file across 1 block", decision.Signal);
        // Clock's block has the only reference into a changed block, so it follows the decision among the surprises.
        Assert.Equal(["decision", "node"], cs.Path.Where(s => s.Tier == "first").Select(s => s.Kind));
        // Other's edit is its own, so its block and file make the second tier; Caller's is a consequence, so it is set aside.
        var then = cs.Path.Where(s => s.Tier == "then").ToList();
        Assert.Equal(["node", "file"], then.Select(s => s.Kind));
        Assert.Equal(["c", "src/Other.cs"], then.Select(s => s.Target));
        Assert.Equal("c", then[1].Under);
        var aside = Assert.Single(cs.Path, s => s.Tier == "aside");
        Assert.Equal("consequences", aside.Target);
        Assert.Equal(["src/Caller.cs"], aside.Files);
        Assert.Equal("1 file of consequences in 1 block", aside.Signal);
    }

    [Fact]
    public void TheFirstTwoTiersHoldAtMostFortySteps()
    {
        var baseFacts = Checker.Run(TestData.Map(), TestData.Analysis(), new CheckOptions { Commit = "base" });
        var changes = Enumerable.Range(0, 60).Select(i => new GitChange($"docs/{i:D2}.md", "added", null, null, "b" + i)).ToList();
        var hunks = changes.Select(c => new FileHunks(c.Path, [new Hunk("@@ -0,0 +1 @@", [["+", "x"]])])).ToList();
        var cs = ChangesetBuilder.Build(baseFacts, Checker.Run(TestData.Map(), TestData.Analysis()), hunks, changes);
        var steps = cs.Path.Where(s => s.Tier != "aside").ToList();
        Assert.Equal(ReadingPath.MostSteps, steps.Count);
        Assert.All(steps, s => Assert.Equal("file", s.Kind));
        Assert.Contains("added, outside the map", steps[0].Signal);
    }

    [Fact]
    public void ThePathRoundTripsThroughJson()
    {
        var map = Map();
        var baseFacts = Checker.Run(map, TestData.Analysis(), new CheckOptions { Commit = "base" });
        var head = TestData.Analysis([new RawDependency(TestData.Other, TestData.Thing, "calls", "A.X.Thing.Back", TestData.Y1, 2)]);
        var cs = ChangesetBuilder.Build(baseFacts, Checker.Run(map, Changed(head, TestData.Y1)));
        var back = FactsJson.Deserialize<Changeset>(FactsJson.Serialize(cs));
        Assert.Equal(FactsJson.Serialize(cs.Path), FactsJson.Serialize(back.Path));
        Assert.Contains(back.Path, s => s.Kind == "cycle" && s.Members is { Count: 2 });
    }
}
