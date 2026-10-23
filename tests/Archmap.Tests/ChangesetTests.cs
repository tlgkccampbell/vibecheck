using Archmap.Analysis;
using Archmap.Checking;
using Archmap.Diffing;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

public class ChangesetTests
{
    private static Facts Base() => Checker.Run(TestData.Map(), TestData.Analysis(), new CheckOptions { Commit = "base" });

    private static ArchMap Relaxed(ArchMap map) => new()
    {
        Solution = map.Solution, Nodes = map.Nodes, Externals = map.Externals, Edges = map.Edges,
        Policies = new PolicySpec { Root = new RootPolicy { Edges = ArchMap.Observed, Externals = ArchMap.Declared } },
    };

    [Fact]
    public void NoChangeProducesEmptyStructureAndUnchangedContent()
    {
        var cs = ChangesetBuilder.Build(Base(), Base());
        Assert.Empty(cs.Structural.Nodes);
        Assert.Empty(cs.Structural.Edges);
        Assert.Empty(cs.Structural.Types);
        Assert.All(cs.Files, f => Assert.Equal("unchanged", f.Status));
        Assert.Equal("unchanged", cs.Content["root"].Status);
        Assert.All(cs.Edges, e => Assert.Equal("unchanged", e.Status));
    }

    [Fact]
    public void JustifiedEdgeAdditionCarriesEvidence()
    {
        var map = TestData.Map();
        map.Edges.Add(new EdgeSpec { From = "b", To = "a" });
        var analysis = TestData.Analysis([new RawDependency(TestData.Service, TestData.Thing, "calls", "A.X.Thing.Go", TestData.B1, 7)]);
        var head = Checker.Run(map, analysis, new CheckOptions { Commit = "head" });
        Assert.Empty(head.Violations);

        var cs = ChangesetBuilder.Build(Base(), head);
        var added = Assert.Single(cs.Structural.Edges);
        Assert.Equal(("b", "a", "added", false), (added.From, added.To, added.Op, added.Gratuitous));
        Assert.Equal(1, added.HeadRefs);
        Assert.Contains("src/B/B1.cs:7 calls A.X.Thing.Go", added.Evidence);
        var edge = cs.Edges.Single(e => e.From == "b" && e.To == "a");
        Assert.Equal("added", edge.Status);
        Assert.False(edge.Undeclared);
    }

    [Fact]
    public void UnjustifiedEdgeAdditionIsGratuitous()
    {
        var map = TestData.Map();
        map.Edges.Add(new EdgeSpec { From = "b", To = "a" });
        var head = Checker.Run(Relaxed(map), TestData.Analysis());
        var cs = ChangesetBuilder.Build(Base(), head);
        Assert.True(Assert.Single(cs.Structural.Edges).Gratuitous);
        Assert.Contains(cs.Structural.Policies, p => p.Level == "root" && p.To == ArchMap.Observed);
    }

    [Fact]
    public void RemovedEdgeStillRealizedIsGratuitous()
    {
        var map = TestData.Map();
        map.Edges.RemoveAll(e => e.From == "b" && e.To == "shared");
        var head = Checker.Run(Relaxed(map), TestData.Analysis());
        var cs = ChangesetBuilder.Build(Base(), head);
        var removed = Assert.Single(cs.Structural.Edges);
        Assert.Equal("removed", removed.Op);
        Assert.True(removed.Gratuitous);
    }

    [Fact]
    public void ContentRollsUpFileChangesThroughTheHierarchy()
    {
        var files = TestData.Analysis().Files.Select(f => f.Path == TestData.Y1 ? f with { Hash = "changed" } : f).ToList();
        var analysis = TestData.Analysis() with { Files = files };
        var head = Checker.Run(TestData.Map(), analysis);
        var cs = ChangesetBuilder.Build(Base(), head);
        Assert.Equal("modified", cs.Files.Single(f => f.Path == TestData.Y1).Status);
        Assert.Equal("modified", cs.Content["a.y"].Status);
        Assert.Equal("children", cs.Content["a"].Status);
        Assert.Equal("children", cs.Content["root"].Status);
        Assert.Equal("unchanged", cs.Content["b"].Status);
        Assert.Equal(1, cs.Content["a"].Modified);
        Assert.Equal(3, cs.Content["a"].Files);
    }

    [Fact]
    public void AFileCountsUnderEveryLeafItsTypesBindTo()
    {
        var analysis = TestData.Analysis(extraTypes: [new TypeInfo("B.InShared", "Example.B", [TestData.S1])]);
        var baseFacts = Checker.Run(TestData.Map(), analysis);
        var changed = analysis with { Files = analysis.Files.Select(f => f.Path == TestData.S1 ? f with { Hash = "changed" } : f).ToList() };
        var cs = ChangesetBuilder.Build(baseFacts, Checker.Run(TestData.Map(), changed));
        Assert.Equal("modified", cs.Content["shared"].Status);
        Assert.Equal("modified", cs.Content["b"].Status);
    }

    [Fact]
    public void AddedLeafBindingNothingIsGratuitous()
    {
        var map = TestData.Map();
        map.Nodes["c"] = new NodeSpec { Name = "C", Bind = ["C.**"] };
        var head = Checker.Run(map, TestData.Analysis());
        var cs = ChangesetBuilder.Build(Base(), head);
        var node = Assert.Single(cs.Structural.Nodes);
        Assert.Equal(("c", "added", true), (node.Id, node.Op, node.Gratuitous));
    }

    [Fact]
    public void RemovedLeafWhosePatternsStillMatchIsGratuitous()
    {
        var baseMap = TestData.Map();
        baseMap.Nodes["c"] = new NodeSpec { Name = "C", Bind = ["B.Service"] };
        var baseFacts = Checker.Run(baseMap, TestData.Analysis());
        Assert.Equal("c", baseFacts.Types.Single(t => t.Name == TestData.Service).Leaf);
        var head = Checker.Run(TestData.Map(), TestData.Analysis());
        var cs = ChangesetBuilder.Build(baseFacts, head);
        var node = Assert.Single(cs.Structural.Nodes);
        Assert.Equal(("c", "removed", true), (node.Id, node.Op, node.Gratuitous));
        var move = Assert.Single(cs.Structural.Types);
        Assert.Equal((TestData.Service, "c", "b"), (move.Name, move.LeafBase, move.LeafHead));
    }

    [Fact]
    public void TypeMovedByAttributeIsStructural()
    {
        var head = Checker.Run(TestData.Map(), TestData.Analysis(adjust: t => t.Name == TestData.Helper ? t with { Attribute = "a.y" } : t));
        var cs = ChangesetBuilder.Build(Base(), head);
        var move = Assert.Single(cs.Structural.Types);
        Assert.Equal((TestData.Helper, "a.x", "a.y", "attribute"), (move.Name, move.LeafBase, move.LeafHead, move.By));
    }

    [Fact]
    public void NewEdgeAtObservedLevelIsFlaggedUndeclared()
    {
        var map = TestData.Map();
        map.Nodes["a"] = map.Nodes["a"] with { Edges = ArchMap.Observed };
        var analysis = TestData.Analysis([new RawDependency(TestData.Other, TestData.Thing, "calls", "A.X.Thing.Back", TestData.Y1, 2)]);
        var head = Checker.Run(map, analysis);
        Assert.Empty(head.Violations);
        var baseFacts = Checker.Run(map, TestData.Analysis());
        var cs = ChangesetBuilder.Build(baseFacts, head);
        var edge = cs.Edges.Single(e => e.From == "a.y" && e.To == "a.x");
        Assert.Equal("added", edge.Status);
        Assert.True(edge.Undeclared);
    }

    [Fact]
    public void EdgeLabelChangesAreStructural()
    {
        var map = TestData.Map();
        map.Edges.RemoveAll(e => e.From == "a" && e.To == "shared");
        map.Edges.Add(new EdgeSpec { From = "a", To = "shared", Label = "Uses primitives" });
        var cs = ChangesetBuilder.Build(Base(), Checker.Run(map, TestData.Analysis()));
        var change = Assert.Single(cs.Structural.Edges);
        Assert.Equal("modified", change.Op);
        Assert.Contains("label none → Uses primitives", change.Detail);
    }

    [Fact]
    public void ChangesetRoundTripsThroughJson()
    {
        var cs = ChangesetBuilder.Build(Base(), Base(), [new FileHunks("a.cs", [new Hunk("@@ -1 +1 @@", [["-", "x"], ["+", "y"]])])]);
        var back = FactsJson.Deserialize<Changeset>(FactsJson.Serialize(cs));
        Assert.Equal("base", back.Base);
        Assert.Equal("y", back.Hunks[0].Hunks[0].Lines[1][1]);
    }
}
