using Archmap.Analysis;
using Archmap.Checking;
using Archmap.Diffing;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

public class ChangesetTests
{
    private static Facts Base() => Checker.Run(TestData.Map(), TestData.Analysis(), new CheckOptions { Commit = "base" });

    [Fact]
    public void NoChangeProducesEmptyStructureAndUnchangedContent()
    {
        var cs = ChangesetBuilder.Build(Base(), Base());
        Assert.Empty(cs.Structural.Nodes);
        Assert.Empty(cs.Structural.Views);
        Assert.Empty(cs.Structural.Types);
        Assert.All(cs.Files, f => Assert.Equal("unchanged", f.Status));
        Assert.Equal("unchanged", cs.Content["root"].Status);
        Assert.All(cs.Edges, e => Assert.Equal("unchanged", e.Status));
        Assert.Equal(5, cs.Edges.Count);
    }

    [Fact]
    public void ANewReferenceIsANewLeafEdgeWithCounts()
    {
        var analysis = TestData.Analysis([new RawDependency(TestData.Service, TestData.Thing, "calls", "A.X.Thing.Go", TestData.B1, 7)]);
        var cs = ChangesetBuilder.Build(Base(), Checker.Run(TestData.Map(), analysis, new CheckOptions { Commit = "head" }));
        var edge = cs.Edges.Single(e => e.From == "b" && e.To == "a.x");
        Assert.Equal((0, 1, "added"), (edge.Base, edge.Head, edge.Status));
        Assert.Empty(cs.Structural.Nodes);
    }

    [Fact]
    public void ARemovedReferenceAndAChangedCountAreReported()
    {
        var more = TestData.Analysis([new RawDependency(TestData.Thing, TestData.Result, "calls", "Shared.Result.Ok", TestData.X1, 13)]);
        var baseFacts = Checker.Run(TestData.Map(), more);
        var fewer = TestData.Analysis() with { Dependencies = TestData.Analysis().Dependencies.Where(d => d.ToType != TestData.Error).ToList() };
        var cs = ChangesetBuilder.Build(baseFacts, Checker.Run(TestData.Map(), fewer));
        Assert.Equal((2, 1, "modified"), cs.Edges.Where(e => e.From == "a.x" && e.To == "shared").Select(e => (e.Base, e.Head, e.Status)).Single());
        Assert.Equal((1, 0, "removed"), cs.Edges.Where(e => e.From == "b" && e.To == "shared").Select(e => (e.Base, e.Head, e.Status)).Single());
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
    public void AddedAndRemovedLeavesAreStructuralAndMoveTheirTypes()
    {
        var baseMap = TestData.Map();
        baseMap.Nodes["c"] = new NodeSpec { Name = "C", Bind = ["B.Service"] };
        var baseFacts = Checker.Run(baseMap, TestData.Analysis());
        Assert.Equal("c", baseFacts.Types.Single(t => t.Name == TestData.Service).Leaf);
        var cs = ChangesetBuilder.Build(baseFacts, Checker.Run(TestData.Map(), TestData.Analysis()));
        var node = Assert.Single(cs.Structural.Nodes);
        Assert.Equal(("c", "removed", "bound 1 type(s)"), (node.Id, node.Op, node.Detail));
        var move = Assert.Single(cs.Structural.Types);
        Assert.Equal((TestData.Service, "c", "b"), (move.Name, move.LeafBase, move.LeafHead));
        Assert.Equal("removed", cs.Content["c"].Status);

        var reverse = ChangesetBuilder.Build(Checker.Run(TestData.Map(), TestData.Analysis()), baseFacts);
        Assert.Equal(("c", "added", "binds 1 type(s)"), reverse.Structural.Nodes.Select(n => (n.Id, n.Op, n.Detail)).Single());
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
    public void ATypeFallingIntoUnsortedIsAMoveAndUnsortedGetsContent()
    {
        var map = TestData.Map();
        map.Nodes.Remove("b");
        var head = Checker.Run(map, TestData.Analysis());
        var cs = ChangesetBuilder.Build(Base(), head);
        var move = Assert.Single(cs.Structural.Types);
        Assert.Equal((TestData.Service, "b", ArchMap.Unsorted), (move.Name, move.LeafBase, move.LeafHead));
        Assert.Equal(1, cs.Content[ArchMap.Unsorted].Files);
        Assert.Equal("removed", cs.Content["b"].Status);
    }

    [Fact]
    public void NodeAndViewModificationsAreDescribed()
    {
        var map = TestData.Map();
        map.Nodes["b"] = map.Nodes["b"] with { Name = "Backend", Purpose = "Serves." };
        map.Views["v"] = new ViewSpec { Name = "V", Level = Identifiers.Root, Groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal) { ["all"] = new GroupSpec { Name = "All", Leaves = ["a.x", "a.y", "b", "shared"] } } };
        var head = Checker.Run(map, TestData.Analysis());
        var cs = ChangesetBuilder.Build(Base(), head);
        var node = Assert.Single(cs.Structural.Nodes);
        Assert.Equal(("b", "modified", "name B → Backend; purpose changed"), (node.Id, node.Op, node.Detail));
        Assert.Equal(("v", "added"), cs.Structural.Views.Select(v => (v.Id, v.Op)).Single());

        var regrouped = map.Clone();
        regrouped.Views["v"] = map.Views["v"] with { Groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal) { ["all"] = new GroupSpec { Name = "All", Leaves = ["a.x", "a.y", "b"] }, ["s"] = new GroupSpec { Name = "S", Leaves = ["shared"] } } };
        var cs2 = ChangesetBuilder.Build(head, Checker.Run(regrouped, TestData.Analysis()));
        var view = Assert.Single(cs2.Structural.Views);
        Assert.Equal(("v", "modified", "groups changed: all, s"), (view.Id, view.Op, view.Detail));
    }

    [Fact]
    public void ChangesetRoundTripsThroughJson()
    {
        var cs = ChangesetBuilder.Build(Base(), Base(), [new FileHunks("a.cs", [new Hunk("@@ -1 +1 @@", [["-", "x"], ["+", "y"]])])]);
        var back = FactsJson.Deserialize<Changeset>(FactsJson.Serialize(cs));
        Assert.Equal("base", back.Base);
        Assert.Equal("y", back.Hunks[0].Hunks[0].Lines[1][1]);
        Assert.Equal(cs.Edges.Count, back.Edges.Count);
    }
}
