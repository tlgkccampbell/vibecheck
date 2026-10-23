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
        Assert.All(facts.Files, f => Assert.NotNull(f.Leaf));
        Assert.Equal(4, facts.Dependencies.Count);
    }

    [Fact]
    public void LiftsDependenciesToLowestCommonAncestor()
    {
        var facts = Run();
        var cross = facts.Dependencies.Single(d => d.From == TestData.X1 && d.To == TestData.S1);
        Assert.Equal("root", cross.Level);
        Assert.Equal(["a", "shared"], cross.Edge!);
        var sibling = facts.Dependencies.Single(d => d.From == TestData.X1 && d.To == TestData.Y1);
        Assert.Equal("a", sibling.Level);
        Assert.Equal(["a.x", "a.y"], sibling.Edge!);
        var intra = facts.Dependencies.Single(d => d.To == TestData.X2);
        Assert.Equal("a.x", intra.Level);
        Assert.Null(intra.Edge);
    }

    [Fact]
    public void PermissionFailsOnUndeclaredEdgeAtDeclaredLevelAndProposesIt()
    {
        var analysis = TestData.Analysis([new RawDependency(TestData.B1, TestData.X1, "calls", "A.X.Thing.Go", "Thing", 7)]);
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

        var ok = Run(map, TestData.Analysis([new RawDependency(TestData.Y1, TestData.S1, "references", "Shared.Money", "Money", 3)]));
        Assert.Empty(ok.Violations);
    }

    [Fact]
    public void PartitionFailsOnUnmappedFileAndProposesLeaf()
    {
        var analysis = TestData.Analysis(extraFiles: [new SourceFile("src/C/Deep/C1.cs", "h9", "C")]);
        var v = Single(Run(analysis: analysis), "partition");
        Assert.Contains("unmapped", v.Message);
        Assert.Equal("add-leaf", v.Proposal!.Op);
        Assert.Equal("deep", v.Proposal.Node);
        Assert.Equal(["src/C/Deep/**/*.cs"], v.Proposal.Bind);
    }

    [Fact]
    public void PartitionProposalNestsBesideClosestLeaf()
    {
        var analysis = TestData.Analysis(extraFiles: [new SourceFile("src/A/Z/Z1.cs", "h9", "A")]);
        var v = Single(Run(analysis: analysis), "partition");
        Assert.Equal("a.z", v.Proposal!.Node);
        var fixedMap = Proposals.Apply(TestData.Map(), [v.Proposal]);
        Assert.Empty(Checker.Run(fixedMap, analysis).Violations);
    }

    [Fact]
    public void PartitionFailsOnAmbiguousFile()
    {
        var map = TestData.Map();
        map.Nodes["b2"] = new NodeSpec { Name = "B2", Bind = ["src/B/*.cs"] };
        var facts = Run(map);
        var v = Assert.Single(facts.Violations, v => v.Rule == "partition");
        Assert.Contains("ambiguous", v.Message);
        Assert.Null(v.Proposal);
        // An ambiguous file binds nowhere, so the edge its references realized is now dead.
        Assert.Contains(facts.Violations, v => v.Rule == "realization" && v.Message.Contains("b → shared"));
    }

    [Fact]
    public void CohesionFailsWhenPartialTypeSpansLeaves()
    {
        var analysis = TestData.Analysis(partials: [new PartialType("A.Service", [TestData.X1, TestData.Y1])]);
        var v = Single(Run(analysis: analysis), "cohesion");
        Assert.Contains("A.Service", v.Message);
        var same = TestData.Analysis(partials: [new PartialType("A.Other", [TestData.X1, TestData.X2])]);
        Assert.Empty(Run(analysis: same).Violations);
    }

    [Fact]
    public void ForbidPolicyMatchesLiftedEdges()
    {
        var map = TestData.Map();
        map.Policies.Forbid.Add(new ForbidSpec { From = "shared", To = "*" });
        map.Edges.Add(new EdgeSpec { From = "shared", To = "b" });
        var analysis = TestData.Analysis([new RawDependency(TestData.S1, TestData.B1, "references", "B.Thing", "Thing", 1)]);
        var v = Single(Run(map, analysis), "policy");
        Assert.Contains("forbidden by policy shared → *", v.Message);
    }

    [Fact]
    public void AcyclicPolicyReportsCyclesAndMetricsRecordThem()
    {
        var map = TestData.Map();
        map.Policies.Acyclic.Add("root");
        map.Edges.Add(new EdgeSpec { From = "shared", To = "b" });
        var analysis = TestData.Analysis([new RawDependency(TestData.S1, TestData.B1, "references", "B.Thing", "Thing", 1)]);
        var facts = Run(map, analysis);
        var v = Single(facts, "policy");
        Assert.Contains("form a cycle", v.Message);
        Assert.Contains("b", v.Message);
        Assert.Contains("shared", v.Message);
        Assert.Single(facts.Metrics["root"].Cycles);
    }

    [Fact]
    public void ExternalsPolicyFailsOnUnboundAssemblyWhenDeclared()
    {
        var analysis = TestData.Analysis(extraExternals: [new ExternalReference(TestData.B1, "Other.Lib", "Other.Lib.Api", 2)]);
        var v = Single(Run(analysis: analysis), "externals");
        Assert.Contains("Other.Lib", v.Message);

        var map = TestData.Map();
        var relaxed = new ArchMap { Solution = map.Solution, Nodes = map.Nodes, Externals = map.Externals, Edges = map.Edges, Policies = new PolicySpec { Root = new RootPolicy { Edges = ArchMap.Declared, Externals = ArchMap.Observed } } };
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
    public void WellFormednessCatchesStructuralMistakes()
    {
        var map = TestData.Map();
        map.Nodes["orphan.child"] = new NodeSpec { Name = "Orphan", Bind = ["src/O/**"] };
        map.Nodes["empty"] = new NodeSpec { Name = "Empty" };
        map.Edges.Add(new EdgeSpec { From = "a.x", To = "b" });
        map.Edges.Add(new EdgeSpec { From = "a", To = "shared" });
        var facts = Run(map);
        Assert.All(facts.Violations, v => Assert.Equal("well-formedness", v.Rule));
        Assert.Contains(facts.Violations, v => v.Message.Contains("parent \"orphan\" does not exist"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("container with no children"));
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

    private const string Gen = "src/A/Generated/Gen.Assembly/Gen.Assembly.Generator/X1.g.cs";
    private static readonly GeneratedFile GenFile = new(Gen, "hg", "A", "Gen.Assembly.Generator");

    [Fact]
    public void UnboundGeneratedFileIsNotSource()
    {
        var analysis = TestData.Analysis(
            [new RawDependency(TestData.B1, Gen, "calls", "A.X.Thing.Generated", "Thing", 9), new RawDependency(Gen, TestData.S1, "references", "Shared.Result", "Result", 1)],
            generated: [GenFile]);
        var facts = Run(analysis: analysis);
        Assert.Empty(facts.Violations);
        Assert.Empty(facts.Warnings);
        Assert.DoesNotContain(facts.Files, f => f.Path == Gen);
        var u = Assert.Single(facts.Unresolvable);
        Assert.Equal((TestData.B1, "Gen.Assembly.Generator", 9), (u.From, u.Generator, u.Line));
        Assert.DoesNotContain(facts.Dependencies, d => d.From == Gen);
    }

    [Fact]
    public void BoundGeneratedFileIsSourceAndItsReferencesLift()
    {
        var map = TestData.Map();
        map.Nodes["a.x"] = map.Nodes["a.x"] with { Bind = ["src/A/X/**/*.cs", "src/A/Generated/**/*.cs"] };
        map.Edges.Add(new EdgeSpec { From = "b", To = "a" });
        var analysis = TestData.Analysis(
            [new RawDependency(TestData.B1, Gen, "calls", "A.X.Thing.Generated", "Thing", 9), new RawDependency(Gen, TestData.S1, "references", "Shared.Result", "Result", 1)],
            generated: [GenFile]);
        var facts = Run(map, analysis);
        Assert.Empty(facts.Violations);
        Assert.Empty(facts.Unresolvable);
        var file = Assert.Single(facts.Files, f => f.Path == Gen);
        Assert.Equal(("a.x", "Gen.Assembly.Generator"), (file.Leaf, file.Generator));
        Assert.Contains(facts.Dependencies, d => d.To == Gen && d.Edge is ["b", "a"]);
        Assert.Contains(facts.Dependencies, d => d.From == Gen && d.Edge is ["a", "shared"]);
    }

    [Fact]
    public void GeneratedPartsCountForCohesionOnlyWhenBound()
    {
        var analysis = TestData.Analysis(partials: [new PartialType("A.X.Ctx", [TestData.X1, Gen])], generated: [GenFile]);
        Assert.Empty(Run(analysis: analysis).Violations);
        var map = TestData.Map();
        map.Nodes["a.y"] = map.Nodes["a.y"] with { Bind = ["src/A/Y/**/*.cs", "src/A/Generated/**/*.cs"] };
        var v = Single(Run(map, analysis), "cohesion");
        Assert.Contains("A.X.Ctx", v.Message);
    }

    [Fact]
    public void ExternalRefsAreAggregatedPerFileAndAssembly()
    {
        var analysis = TestData.Analysis(extraExternals: [new ExternalReference(TestData.X1, "Ext.Sdk", "Ext.Sdk.Other", 30)]);
        var facts = Run(analysis: analysis);
        var e = Assert.Single(facts.ExternalRefs);
        Assert.Equal(2, e.Count);
        Assert.Equal(20, e.Line);
        Assert.Equal(2, facts.Metrics["root"].Crossing);
    }

    [Fact]
    public void DependenciesOnUnlistedFilesAreWarnedAboutNotDropped()
    {
        var analysis = TestData.Analysis([new RawDependency(TestData.X1, "src/Ghost.cs", "references", "Ghost", "Ghost", 1)]);
        var facts = Run(analysis: analysis);
        Assert.Empty(facts.Violations);
        Assert.Contains(facts.Warnings, w => w.Contains("analysis bug: 1 dependencies"));
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
            new ProposalFact("add-leaf", Node: "c", Name: "C", Bind: ["src/C/**/*.cs"]),
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
        Assert.Equal(facts.Map.Edges.Count, back.Map.Edges.Count);
        Assert.Equal("a.x", back.Map.Nodes["a.x"] is { IsLeaf: true } ? "a.x" : "");
        Assert.Equal(ArchMap.Declared, back.Map.Policies.Root.Edges);
    }
}
