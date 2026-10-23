using Archmap.Analysis;
using Archmap.Checking;
using Archmap.Format;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

public class CheckerTests
{
    private static Facts Run(ArchMap? map = null, AnalysisResult? analysis = null, CheckOptions? options = null) =>
        Checker.Run(map ?? TestData.Map(), analysis ?? TestData.Analysis(), options);

    private static ViolationFact Single(Facts facts, string rule)
    {
        var v = Assert.Single(facts.Violations);
        Assert.Equal(rule, v.Rule);
        return v;
    }

    [Fact]
    public void BaselinePasses()
    {
        var facts = Run();
        Assert.Empty(facts.Violations);
        Assert.All(facts.Types, t => { Assert.NotNull(t.Leaf); Assert.Equal("pattern", t.By); });
        Assert.Equal(4, facts.Dependencies.Count);
    }

    [Fact]
    public void LiftsDependenciesToLowestCommonAncestor()
    {
        var facts = Run();
        var cross = facts.Dependencies.Single(d => d.From == TestData.Thing && d.To == TestData.Result);
        Assert.Equal("root", cross.Level);
        Assert.Equal(["a", "shared"], cross.Edge!);
        var sibling = facts.Dependencies.Single(d => d.From == TestData.Thing && d.To == TestData.Other);
        Assert.Equal("a", sibling.Level);
        Assert.Equal(["a.x", "a.y"], sibling.Edge!);
        var intra = facts.Dependencies.Single(d => d.To == TestData.Helper);
        Assert.Equal("a.x", intra.Level);
        Assert.Null(intra.Edge);
    }

    [Fact]
    public void FilesListTheLeavesOfTheirTypes()
    {
        var analysis = TestData.Analysis(extraTypes: [new TypeInfo("A.Y.InX", "Example.A", [TestData.X1])]);
        var facts = Run(analysis: analysis);
        Assert.Equal(["a.x", "a.y"], facts.Files.Single(f => f.Path == TestData.X1).Leaves);
        Assert.Equal(["shared"], facts.Files.Single(f => f.Path == TestData.S1).Leaves);
    }

    [Fact]
    public void PermissionFailsOnUndeclaredEdgeAtDeclaredLevelAndProposesIt()
    {
        var analysis = TestData.Analysis([new RawDependency(TestData.Service, TestData.Thing, "calls", "A.X.Thing.Go", TestData.B1, 7)]);
        var v = Single(Run(analysis: analysis), "permission");
        Assert.Contains("b → a is not declared at level root", v.Message);
        Assert.Contains("src/B/B1.cs:7", v.Message);
        Assert.Equal(new ProposalFact("add-edge", From: "b", To: "a"), v.Proposal);
    }

    [Fact]
    public void PermissionIsSkippedAtObservedLevels()
    {
        var map = TestData.Map();
        map.Nodes["a"] = map.Nodes["a"] with { Edges = ArchMap.Observed };
        map.Edges.RemoveAll(e => e.From == "a.x");
        Assert.Empty(Run(map).Violations);
    }

    [Fact]
    public void RealizationFailsOnDeadEdgeAndProposesRemoval()
    {
        var map = TestData.Map();
        map.Edges.Add(new EdgeSpec { From = "b", To = "a" });
        var v = Single(Run(map), "realization");
        Assert.Contains("b → a is declared at level root but no reference realizes it", v.Message);
        Assert.Equal(new ProposalFact("remove-edge", From: "b", To: "a"), v.Proposal);
    }

    [Fact]
    public void AssertedEdgesAreExemptFromRealization()
    {
        var map = TestData.Map();
        map.Edges.Add(new EdgeSpec { From = "b", To = "a", Asserted = true });
        Assert.Empty(Run(map).Violations);
    }

    [Fact]
    public void PayloadMustBeReferencedAcrossTheEdge()
    {
        var map = TestData.Map();
        map.Edges.RemoveAll(e => e.From == "a" && e.To == "shared");
        map.Edges.Add(new EdgeSpec { From = "a", To = "shared", Payload = "Money" });
        var v = Single(Run(map), "realization");
        Assert.Contains("declares payload Money", v.Message);
        Assert.Null(v.Proposal);

        var analysis = TestData.Analysis(
            [new RawDependency(TestData.Other, "Shared.Money", "references", "Shared.Money", TestData.Y1, 3)],
            extraTypes: [new TypeInfo("Shared.Money", "Example.Shared", [TestData.S1])]);
        Assert.Empty(Run(map, analysis).Violations);
    }

    [Fact]
    public void PartitionFailsOnUnmappedTypeAndProposesLeafAtTopLevel()
    {
        var analysis = TestData.Analysis(extraTypes: [new TypeInfo("C.Deep.Thing", "Example.C", ["src/C/Deep/C1.cs"])]);
        var v = Single(Run(analysis: analysis), "partition");
        Assert.Contains("C.Deep.Thing is unmapped", v.Message);
        Assert.Equal(("add-leaf", "deep", "Deep"), (v.Proposal!.Op, v.Proposal.Node, v.Proposal.Name));
        Assert.Equal(["C.Deep.**"], v.Proposal.Bind);
    }

    [Fact]
    public void PartitionProposalNestsBesideClosestLeaf()
    {
        var analysis = TestData.Analysis(extraTypes: [new TypeInfo("A.Z.Thing", "Example.A", ["src/A/Z/Z1.cs"])]);
        var v = Single(Run(analysis: analysis), "partition");
        Assert.Equal("a.z", v.Proposal!.Node);
        Assert.Equal(["A.Z.**"], v.Proposal.Bind);
        var fixedMap = Proposals.Apply(TestData.Map(), [v.Proposal]);
        Assert.Empty(Checker.Run(fixedMap, analysis).Violations);
    }

    [Fact]
    public void GlobalNamespaceTypeIsProposedByAssembly()
    {
        var analysis = TestData.Analysis(extraTypes: [new TypeInfo("Program", "Example.B", ["src/B/Program.cs"])]);
        var v = Single(Run(analysis: analysis), "partition");
        Assert.Equal(("add-leaf", "global", "global"), (v.Proposal!.Op, v.Proposal.Node, v.Proposal.Name));
        Assert.Equal(["Example.B::*"], v.Proposal.Bind);
    }

    [Fact]
    public void EquallySpecificPatternsMakeATypeAmbiguous()
    {
        var map = TestData.Map();
        map.Nodes["b2"] = new NodeSpec { Name = "B2", Bind = ["B.*"] };
        var facts = Run(map);
        var v = Assert.Single(facts.Violations, v => v.Rule == "partition");
        Assert.Contains("B.Service is ambiguous", v.Message);
        Assert.Contains("b (B.**)", v.Message);
        Assert.Contains("b2 (B.*)", v.Message);
        Assert.Null(v.Proposal);
        // An ambiguous type binds nowhere, so the edge its references realized is now dead.
        Assert.Contains(facts.Violations, v => v.Rule == "realization" && v.Message.Contains("b → shared"));
    }

    [Fact]
    public void MostSpecificPatternWinsWithoutExclusions()
    {
        var map = TestData.Map();
        map.Nodes["b.service"] = new NodeSpec { Name = "Service", Bind = ["B.Service"] };
        map.Nodes["b.rest"] = new NodeSpec { Name = "Rest", Bind = ["B.**"] };
        map.Nodes["b"] = new NodeSpec { Name = "B" };
        var facts = Run(map);
        Assert.Empty(facts.Violations);
        Assert.Equal("b.service", facts.Types.Single(t => t.Name == TestData.Service).Leaf);
    }

    [Fact]
    public void AttributeOverridesPatternsAndIsCounted()
    {
        // Helper moves to b by attribute, so the reference from Thing to it now crosses
        // from a to b, which the map must declare.
        var map = TestData.Map();
        map.Edges.Add(new EdgeSpec { From = "a", To = "b" });
        var analysis = TestData.Analysis(adjust: t => t.Name == TestData.Helper ? t with { Attribute = "b" } : t);
        var facts = Run(map, analysis);
        Assert.Empty(facts.Violations);
        var helper = facts.Types.Single(t => t.Name == TestData.Helper);
        Assert.Equal(("b", "attribute"), (helper.Leaf, helper.By));
        Assert.Equal(1, facts.Metrics["root"].Attributed);
        Assert.Equal(0, facts.Metrics["a"].Attributed);
        Assert.Contains(facts.Dependencies, d => d.To == TestData.Helper && d.Level == "root" && d.Edge is ["a", "b"]);
    }

    [Fact]
    public void AttributeReferenceCrossingUndeclaredLevelFailsPermission()
    {
        var map = TestData.Map();
        map.Edges.RemoveAll(e => e.From == "a" && e.To == "shared");
        var analysis = TestData.Analysis(adjust: t => t.Name == TestData.Result ? t with { Attribute = "b" } : t);
        var v = Single(Run(map, analysis), "permission");
        Assert.Contains("a → b is not declared", v.Message);
    }

    [Fact]
    public void BadAttributesAreMisattributed()
    {
        var unknown = TestData.Analysis(adjust: t => t.Name == TestData.Helper ? t with { Attribute = "nope" } : t);
        var v1 = Single(Run(analysis: unknown), "partition");
        Assert.Contains("names no leaf", v1.Message);
        Assert.Null(v1.Proposal);

        var nested = TestData.Analysis(adjust: t => t.Name == TestData.Helper ? t with { MisattributedNested = "A.X.Helper.Inner" } : t);
        var v2 = Single(Run(analysis: nested), "partition");
        Assert.Contains("nested type A.X.Helper.Inner", v2.Message);
    }

    [Fact]
    public void ForbidPolicyMatchesLiftedEdges()
    {
        var map = TestData.Map();
        map.Policies.Forbid.Add(new ForbidSpec { From = "shared", To = "*" });
        map.Edges.Add(new EdgeSpec { From = "shared", To = "b" });
        var analysis = TestData.Analysis([new RawDependency(TestData.Result, TestData.Service, "references", "B.Service", TestData.S1, 1)]);
        var v = Single(Run(map, analysis), "policy");
        Assert.Contains("forbidden by policy shared → *", v.Message);
    }

    [Fact]
    public void AcyclicPolicyReportsCyclesAndMetricsRecordThem()
    {
        var map = TestData.Map();
        map.Policies.Acyclic.Add("root");
        map.Edges.Add(new EdgeSpec { From = "shared", To = "b" });
        var analysis = TestData.Analysis([new RawDependency(TestData.Result, TestData.Service, "references", "B.Service", TestData.S1, 1)]);
        var facts = Run(map, analysis);
        var v = Single(facts, "policy");
        Assert.Contains("form a cycle", v.Message);
        Assert.Single(facts.Metrics["root"].Cycles);
    }

    [Fact]
    public void ExternalsPolicyFailsOnUnboundAssemblyWhenDeclared()
    {
        var analysis = TestData.Analysis(extraExternals: [new ExternalReference(TestData.Service, "Other.Lib", "Other.Lib.Api", TestData.B1, 2)]);
        var v = Single(Run(analysis: analysis), "externals");
        Assert.Contains("Other.Lib", v.Message);

        var relaxed = TestData.Map();
        relaxed = new ArchMap { Solution = relaxed.Solution, Nodes = relaxed.Nodes, Externals = relaxed.Externals, Edges = relaxed.Edges, Policies = new PolicySpec { Root = new RootPolicy { Edges = ArchMap.Declared, Externals = ArchMap.Observed } } };
        var facts = Run(relaxed, analysis);
        Assert.Empty(facts.Violations);
        Assert.Contains(facts.ExternalRefs, e => e.Assembly == "Other.Lib" && e.External is null);
    }

    [Fact]
    public void ExternalEdgesArePermittedAndRealizedAtRoot()
    {
        var map = TestData.Map();
        map.Edges.RemoveAll(e => e.To == "ext");
        var v = Single(Run(map), "permission");
        Assert.Contains("a → ext", v.Message);
    }

    [Fact]
    public void MostSpecificExternalPatternWins()
    {
        var map = TestData.Map();
        map.Externals["sdk"] = new ExternalSpec { Name = "Sdk", Bind = ["Ext.Sdk"] };
        map.Edges.RemoveAll(e => e.To == "ext");
        map.Edges.Add(new EdgeSpec { From = "a", To = "sdk" });
        var facts = Run(map);
        Assert.Empty(facts.Violations);
        Assert.Equal("sdk", facts.ExternalRefs.Single().External);
    }

    [Fact]
    public void ExternalRefsAreAggregatedPerTypeAndAssembly()
    {
        var analysis = TestData.Analysis(extraExternals: [new ExternalReference(TestData.Thing, "Ext.Sdk", "Ext.Sdk.Other", TestData.X1, 30)]);
        var facts = Run(analysis: analysis);
        var e = Assert.Single(facts.ExternalRefs);
        Assert.Equal(2, e.Count);
        Assert.Equal(20, e.Line);
        Assert.Equal(2, facts.Metrics["root"].Crossing);
    }

    [Fact]
    public void DependenciesOnUnlistedTypesAreWarnedAboutNotDropped()
    {
        var analysis = TestData.Analysis([new RawDependency(TestData.Thing, "Ghost.Type", "references", "Ghost.Type", TestData.X1, 1)]);
        var facts = Run(analysis: analysis);
        Assert.Empty(facts.Violations);
        Assert.Contains(facts.Warnings, w => w.Contains("analysis bug: 1 dependencies"));
    }

    [Fact]
    public void WellFormednessCatchesStructuralMistakesAndBadPatterns()
    {
        var map = TestData.Map();
        map.Nodes["orphan.child"] = new NodeSpec { Name = "Orphan", Bind = ["O.**"] };
        map.Nodes["empty"] = new NodeSpec { Name = "Empty" };
        map.Nodes["bad"] = new NodeSpec { Name = "Bad", Bind = ["A::B::C"] };
        map.Edges.Add(new EdgeSpec { From = "a.x", To = "b" });
        map.Edges.Add(new EdgeSpec { From = "a", To = "shared" });
        var facts = Run(map);
        Assert.All(facts.Violations, v => Assert.Equal("well-formedness", v.Rule));
        Assert.Contains(facts.Violations, v => v.Message.Contains("parent \"orphan\" does not exist"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("container with no children"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("more than one assembly qualifier"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("must be siblings"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("declared twice"));
        Assert.Empty(facts.Dependencies);
    }

    [Fact]
    public void WellFormednessRequiresCanonicalText()
    {
        var map = TestData.Map();
        var canonical = MapWriter.Write(map);
        Assert.Empty(Run(map, options: new CheckOptions { MapText = canonical }).Violations);
        var v = Single(Run(map, options: new CheckOptions { MapText = canonical.Replace("  ", "    ") }), "well-formedness");
        Assert.Contains("canonical", v.Message);
    }

    [Fact]
    public void LayoutShapeErrorsFailAndUnknownIdentifiersWarn()
    {
        var bad = Single(Run(options: new CheckOptions { LayoutText = "{ \"version\": 0, \"views\": { \"root\": { \"a\": [1] } } }" }), "well-formedness");
        Assert.Contains("[x, y, w, h]", bad.Message);
        var facts = Run(options: new CheckOptions { LayoutText = "{ \"version\": 0, \"views\": { \"root\": { \"ghost\": [1, 2, 3, 4] } } }" });
        Assert.Empty(facts.Violations);
        Assert.Contains(facts.Warnings, w => w.Contains("ghost"));
    }

    [Fact]
    public void MetricsCountInternalAndCrossingDependencies()
    {
        var facts = Run();
        Assert.Equal(4, facts.Metrics["root"].Internal);
        Assert.Equal(1, facts.Metrics["root"].Crossing);
        Assert.Equal(2, facts.Metrics["a"].Internal);
        Assert.Equal(2, facts.Metrics["a"].Crossing);
    }

    [Fact]
    public void ProposalsApplyToACopy()
    {
        var map = TestData.Map();
        var applied = Proposals.Apply(map,
        [
            new ProposalFact("add-edge", From: "b", To: "a"),
            new ProposalFact("remove-edge", From: "a", To: "ext"),
            new ProposalFact("add-leaf", Node: "c", Name: "C", Bind: ["C.**"]),
        ]);
        Assert.NotNull(applied.FindEdge("b", "a"));
        Assert.Null(applied.FindEdge("a", "ext"));
        Assert.True(applied.Nodes["c"].IsLeaf);
        Assert.Null(map.FindEdge("b", "a"));
        Assert.False(map.Nodes.ContainsKey("c"));
    }

    [Fact]
    public void FactsRoundTripThroughJson()
    {
        var facts = Run();
        var json = FactsJson.Serialize(facts);
        var back = FactsJson.Deserialize<Facts>(json);
        Assert.Equal(facts.Dependencies.Count, back.Dependencies.Count);
        Assert.Equal(facts.Types.Count, back.Types.Count);
        Assert.Equal(facts.Map.Edges.Count, back.Map.Edges.Count);
        Assert.True(back.Map.Nodes["a.x"].IsLeaf);
        Assert.Equal(ArchMap.Declared, back.Map.Policies.Root.Edges);
    }
}
