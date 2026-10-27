using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Format;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

public class CheckerTests
{
    private static Facts Run(ArchitectureMap? map = null, AnalysisResult? analysis = null, CheckOptions? options = null) =>
        Checker.Run(map ?? TestData.Map(), analysis ?? TestData.Analysis(), options);

    private static EdgeCount Edge(IEnumerable<EdgeCount> edges, string from, string to) =>
        Assert.Single(edges, e => e.From == from && e.To == to);

    [Fact]
    public void BaselineHasNoWarningsAndBindsEverythingByPattern()
    {
        var facts = Run();
        Assert.Empty(facts.Violations);
        Assert.Empty(facts.Warnings);
        Assert.All(facts.Types, t => Assert.Equal("pattern", t.By));
        Assert.Equal(4, facts.Dependencies.Count);
    }

    [Fact]
    public void LeafEdgesAggregateDependenciesAndBoundExternals()
    {
        var facts = Run();
        Assert.Equal(1, Edge(facts.LeafEdges, "a.x", "a.y").Count);
        Assert.Equal(1, Edge(facts.LeafEdges, "a.x", "a.x").Count);
        Assert.Equal(1, Edge(facts.LeafEdges, "a.x", "shared").Count);
        Assert.Equal(1, Edge(facts.LeafEdges, "b", "shared").Count);
        Assert.Equal(1, Edge(facts.LeafEdges, "a.x", "ext").Count);
        Assert.Equal(5, facts.LeafEdges.Count);
    }

    [Fact]
    public void LevelsLiftLeafEdgesToTheLowestCommonAncestor()
    {
        var facts = Run();
        var root = facts.Levels["root"];
        Assert.Equal(1, Edge(root.Edges, "a", "shared").Count);
        Assert.Equal(1, Edge(root.Edges, "b", "shared").Count);
        Assert.Equal(1, Edge(root.Edges, "a", "ext").Count);
        Assert.Equal(3, root.Edges.Count);
        var a = facts.Levels["a"];
        Assert.Equal(1, Edge(a.Edges, "a.x", "a.y").Count);
        Assert.Single(a.Edges);
        Assert.Empty(root.Cycles);
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
    public void AnUnmappedTypeGoesToTheUnsortedLeafWithAWarning()
    {
        var analysis = TestData.Analysis(
            extraTypes: [new TypeInfo("C.Stray", "Example.A", [TestData.X2])],
            extraDependencies: [new RawDependency("C.Stray", TestData.Result, "references", "Shared.Result", TestData.X2, 3)]);
        var facts = Run(analysis: analysis);
        Assert.Empty(facts.Violations);
        var stray = facts.Types.Single(t => t.Name == "C.Stray");
        Assert.Equal((ArchitectureMap.Unsorted, "fallback"), (stray.Leaf, stray.By));
        Assert.Contains(facts.Warnings, w => w.Contains("1 type(s) are unsorted") && w.Contains("C.Stray"));
        Assert.Equal(1, Edge(facts.LeafEdges, ArchitectureMap.Unsorted, "shared").Count);
        Assert.Equal(1, Edge(facts.Levels["root"].Edges, ArchitectureMap.Unsorted, "shared").Count);
        Assert.Equal(1, facts.Nodes[ArchitectureMap.Unsorted].Types);
        Assert.Equal(["a.x", ArchitectureMap.Unsorted], facts.Files.Single(f => f.Path == TestData.X2).Leaves);
    }

    [Fact]
    public void EquallySpecificPatternsWarnAndTheFirstLeafTakesTheType()
    {
        var map = TestData.Map();
        map.Nodes["b2"] = new NodeSpec { Name = "B2", Bind = ["B.*"] };
        var facts = Run(map);
        Assert.Empty(facts.Violations);
        Assert.Equal("b", facts.Types.Single(t => t.Name == TestData.Service).Leaf);
        var w = Assert.Single(facts.Warnings);
        Assert.Contains("B.Service is claimed equally by b (B.**), b2 (B.*); b takes it", w);
    }

    [Fact]
    public void MostSpecificPatternWinsAndAnExactNameBeatsAWildcard()
    {
        var map = TestData.Map();
        map.Nodes["b.service"] = new NodeSpec { Name = "Service", Bind = ["B.Service"] };
        map.Nodes["b.rest"] = new NodeSpec { Name = "Rest", Bind = ["B.**"] };
        map.Nodes["b"] = new NodeSpec { Name = "B" };
        var facts = Run(map);
        Assert.Empty(facts.Warnings);
        Assert.Equal("b.service", facts.Types.Single(t => t.Name == TestData.Service).Leaf);
    }

    [Fact]
    public void AttributeOverridesPatternsAndIsCounted()
    {
        var analysis = TestData.Analysis(adjust: t => t.Name == TestData.Helper ? t with { Attribute = "b" } : t);
        var facts = Run(analysis: analysis);
        Assert.Empty(facts.Warnings);
        var helper = facts.Types.Single(t => t.Name == TestData.Helper);
        Assert.Equal(("b", "attribute"), (helper.Leaf, helper.By));
        Assert.Equal(1, facts.Nodes["b"].Attributed);
        Assert.Equal(0, facts.Nodes["a"].Attributed);
        Assert.Equal(1, Edge(facts.Levels["root"].Edges, "a", "b").Count);
    }

    [Fact]
    public void BadAttributesWarnAndFallBackToPatterns()
    {
        var unknown = Run(analysis: TestData.Analysis(adjust: t => t.Name == TestData.Helper ? t with { Attribute = "nope" } : t));
        Assert.Empty(unknown.Violations);
        Assert.Equal(("a.x", "pattern"), (unknown.Types.Single(t => t.Name == TestData.Helper).Leaf, unknown.Types.Single(t => t.Name == TestData.Helper).By));
        Assert.Contains(unknown.Warnings, w => w.Contains("names no leaf"));

        var nested = Run(analysis: TestData.Analysis(adjust: t => t.Name == TestData.Helper ? t with { MisattributedNested = "A.X.Helper.Inner" } : t));
        Assert.Empty(nested.Violations);
        Assert.Contains(nested.Warnings, w => w.Contains("A.X.Helper.Inner") && w.Contains("nested types follow their container"));
    }

    [Fact]
    public void MostSpecificExternalPatternWinsAndUnboundAssembliesWarn()
    {
        var map = TestData.Map();
        map.Externals["sdk"] = new ExternalSpec { Name = "Sdk", Bind = ["Ext.Sdk"] };
        Assert.Equal("sdk", Run(map).ExternalRefs.Single().External);

        var analysis = TestData.Analysis(extraExternals: [new ExternalReference(TestData.Service, "Newtonsoft.Json", "Newtonsoft.Json.JsonConvert", "references", "Newtonsoft.Json.JsonConvert", TestData.B1, 9)]);
        var facts = Run(analysis: analysis);
        Assert.Empty(facts.Violations);
        Assert.Null(facts.ExternalRefs.Single(e => e.Assembly == "Newtonsoft.Json").External);
        Assert.Contains(facts.Warnings, w => w.Contains("assembly Newtonsoft.Json is referenced 1 time(s) but no external binds it"));
        Assert.DoesNotContain(facts.LeafEdges, e => e.From == "b" && e.To != "shared");
    }

    [Fact]
    public void ExternalRefsAreAggregatedPerTypeAndReferencedTypeAndCountedPerAssemblyByTheirReaders()
    {
        var analysis = TestData.Analysis(extraExternals:
        [
            new ExternalReference(TestData.Thing, "Ext.Sdk", "Ext.Sdk.Client", "calls", "Ext.Sdk.Client.Send", TestData.X1, 25),
            new ExternalReference(TestData.Thing, "Ext.Sdk", "Ext.Sdk.Other", "references", "Ext.Sdk.Other", TestData.X1, 30),
        ]);
        var facts = Run(analysis: analysis);
        Assert.Equal(["Ext.Sdk.Client", "Ext.Sdk.Other"], facts.ExternalRefs.Select(e => e.Type));
        var client = facts.ExternalRefs[0];
        Assert.Equal((2, 20, "references", "Ext.Sdk.Client"), (client.Count, client.Line, client.Kind, client.Symbol));
        // The leaf edge and the type pairs behind it count every reference to the assembly, as they did when the facts
        // aggregated per assembly.
        Assert.Equal(3, Edge(facts.LeafEdges, "a.x", "ext").Count);
        Assert.Equal([(TestData.Thing, "Ext.Sdk", 3)], Explain.Pairs(facts, "a.x", "ext"));
    }

    [Fact]
    public void DependenciesOnUnlistedTypesAreWarnedAboutNotDropped()
    {
        var analysis = TestData.Analysis([new RawDependency(TestData.Thing, "Ghost.Type", "references", "Ghost.Type", TestData.X1, 1)]);
        var facts = Run(analysis: analysis);
        Assert.Empty(facts.Violations);
        Assert.Contains(facts.Warnings, w => w.Contains("analysis bug: 1 dependencies"));
        Assert.Equal(4, facts.Dependencies.Count);
    }

    [Fact]
    public void WellFormednessFailsOnStructuralMistakesBadPatternsAndReservedNames()
    {
        var map = TestData.Map();
        map.Nodes["orphan.child"] = new NodeSpec { Name = "Orphan", Bind = ["O.**"] };
        map.Nodes["empty"] = new NodeSpec { Name = "Empty" };
        map.Nodes["bad"] = new NodeSpec { Name = "Bad", Bind = ["A::B::C"] };
        map.Nodes[ArchitectureMap.Unsorted] = new NodeSpec { Name = "Mine", Bind = ["U.**"] };
        var facts = Run(map);
        Assert.All(facts.Violations, v => Assert.Equal("well-formedness", v.Rule));
        Assert.Contains(facts.Violations, v => v.Message.Contains("parent \"orphan\" does not exist"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("container with no children"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("more than one assembly qualifier"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("reserved for the automatic unsorted leaf"));
        Assert.Empty(facts.Dependencies);
        Assert.Empty(facts.Types);
    }

    [Fact]
    public void NonCanonicalTextIsAWarning()
    {
        var map = TestData.Map();
        var canonical = MapWriter.Write(map);
        Assert.Empty(Run(map, options: new CheckOptions { MapText = canonical }).Warnings);
        var facts = Run(map, options: new CheckOptions { MapText = canonical.Replace("  ", "    ") });
        Assert.Empty(facts.Violations);
        Assert.Contains(facts.Warnings, w => w.Contains("canonical"));
    }

    [Fact]
    public void LayoutShapeErrorsFailAndUnknownIdentifiersWarn()
    {
        var bad = Run(options: new CheckOptions { LayoutText = "{ \"version\": 0, \"views\": { \"root\": { \"a\": [1] } } }" });
        Assert.Contains(bad.Violations, v => v.Message.Contains("[x, y, w, h]"));
        var facts = Run(options: new CheckOptions { LayoutText = "{ \"version\": 0, \"views\": { \"root\": { \"ghost\": [1, 2, 3, 4] } } }" });
        Assert.Empty(facts.Violations);
        Assert.Contains(facts.Warnings, w => w.Contains("ghost"));
    }

    [Fact]
    public void NodeMetricsCountTypesInternalAndCrossingReferences()
    {
        var facts = Run();
        // Inside a: Thing→Other and Thing→Helper. Crossing a: Thing→Result and Thing→Ext.Sdk.
        Assert.Equal(new NodeMetrics(3, 2, 2, 0), facts.Nodes["a"]);
        Assert.Equal(new NodeMetrics(2, 1, 3, 0), facts.Nodes["a.x"]);
        Assert.Equal(new NodeMetrics(2, 0, 2, 0), facts.Nodes["shared"]);
        Assert.False(facts.Nodes.ContainsKey(ArchitectureMap.Unsorted));
    }

    [Fact]
    public void ReportsACycleAsASetOfMembersWithTheEdgesThatBreakIt()
    {
        // b and c each reference a and are referenced by it, but not each other, so no path runs a → b → c → a.
        var edges = new List<EdgeCount> { new("a", "b", 5), new("b", "a", 1), new("a", "c", 1), new("c", "a", 4) };

        var cycle = Assert.Single(Checker.Cycles(["c", "b", "a"], edges));

        Assert.Equal(["a", "b", "c"], cycle);
        Assert.Equal([new EdgeCount("a", "c", 1), new EdgeCount("b", "a", 1)], Checker.CutSet(cycle, edges));
    }

    [Fact]
    public void CutsTheLightestEdgeOfARing()
    {
        var edges = new List<EdgeCount> { new("a", "b", 3), new("b", "c", 1), new("c", "a", 2), new("x", "a", 9) };

        Assert.Equal([new EdgeCount("b", "c", 1)], Checker.CutSet(["a", "b", "c"], edges));
    }

    [Fact]
    public void EveryCutSetBreaksEveryCycleAndKeepsNoEdgeItCouldReturn()
    {
        var random = new Random(42);
        var (compared, greedy) = (0, 0);
        for (var graph = 0; graph < 340; graph++)
        {
            // The last 40 graphs are larger than the exact search takes, so that the greedy order is checked too.
            var nodes = Enumerable.Range(0, graph < 300 ? random.Next(2, 7) : random.Next(17, 21)).Select(i => $"n{i}").ToList();
            var density = graph < 300 ? 0.4 : 0.15;
            var edges = (from a in nodes from b in nodes where a != b && random.NextDouble() < density select new EdgeCount(a, b, random.Next(1, 10))).ToList();
            foreach (var cycle in Checker.Cycles(nodes, edges))
            {
                var cut = Checker.CutSet(cycle, edges);
                var kept = edges.Where(e => !cut.Contains(e)).ToList();
                Assert.Empty(Checker.Cycles(cycle, kept));
                foreach (var e in cut) Assert.NotEmpty(Checker.Cycles(cycle, [.. kept, e]));
                if (cycle.Count > 16) greedy++;
                if (edges.Count(e => cycle.Contains(e.From) && cycle.Contains(e.To)) > 14) continue;
                Assert.Equal(LightestCut(cycle, edges), cut.Sum(e => e.Count));
                compared++;
            }
        }
        Assert.True(compared > 100 && greedy > 10, $"compared {compared} cuts with the lightest, and checked {greedy} greedy cuts");
    }

    /// <summary>Returns the fewest references that any set of edges among a cycle's members carries whose removal breaks it, by trying every set. It suits a component of a few edges.</summary>
    private static int LightestCut(List<string> members, List<EdgeCount> edges)
    {
        var inside = edges.Where(e => members.Contains(e.From) && members.Contains(e.To)).ToList();
        var best = int.MaxValue;
        for (var mask = 0; mask < 1 << inside.Count; mask++)
        {
            var removed = inside.Where((_, i) => (mask & (1 << i)) != 0).Sum(e => e.Count);
            if (removed >= best) continue;
            if (Checker.Cycles(members, inside.Where((_, i) => (mask & (1 << i)) == 0).ToList()).Count == 0) best = removed;
        }
        return best;
    }

    [Fact]
    public void FindsTheLightestCutOfSixteenMembersQuickly()
    {
        var nodes = Enumerable.Range(0, 16).Select(i => $"n{i:00}").ToList();
        var edges = nodes.SelectMany((a, i) => nodes.Where((_, j) => j != i).Select((b, j) => new EdgeCount(a, b, 1 + (i * 7 + j) % 5))).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var cut = Checker.CutSet(Assert.Single(Checker.Cycles(nodes, edges)), edges);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.Empty(Checker.Cycles(nodes, edges.Where(e => !cut.Contains(e)).ToList()));
    }

    [Fact]
    public void CyclesAmongChildrenAreReportedPerLevel()
    {
        var analysis = TestData.Analysis([new RawDependency(TestData.Other, TestData.Thing, "calls", "A.X.Thing.Back", TestData.Y1, 2)]);
        var facts = Run(analysis: analysis);
        var cycle = Assert.Single(facts.Levels["a"].Cycles);
        Assert.Equal(["a.x", "a.y"], cycle.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Empty(facts.Levels["root"].Cycles);
    }

    [Fact]
    public void ViewsRegroupLeavesAndGetTheSameMetrics()
    {
        var map = TestData.Map();
        map.Views["by-side"] = new ViewSpec
        {
            Name = "By side", Level = Identifiers.Root,
            Groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal)
            {
                ["front"] = new GroupSpec { Name = "Front", Leaves = ["a.x", "b"] },
                ["back"] = new GroupSpec { Name = "Back", Leaves = ["a.y", "shared"] },
            },
        };
        var facts = Run(map);
        Assert.Empty(facts.Violations);
        var view = facts.Views["by-side"];
        Assert.Equal(3, Edge(view.Edges, "front", "back").Count);
        Assert.Single(view.Edges);
        Assert.Empty(view.Cycles);
        // Front holds Thing, Helper and Service: Thing→Helper stays inside; Thing→Other, Thing→Result, Service→Error and Thing→Ext cross.
        Assert.Equal(new NodeMetrics(3, 1, 4, 0), view.Groups["front"]);
    }

    [Fact]
    public void AViewMustPartitionTheLeavesUnderItsLevel()
    {
        var map = TestData.Map();
        map.Views["bad"] = new ViewSpec
        {
            Name = "Bad", Level = "a",
            Groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal)
            {
                ["one"] = new GroupSpec { Name = "One", Leaves = ["a.x", "b"] },
                ["two"] = new GroupSpec { Name = "Two", Leaves = ["a.x"] },
            },
        };
        map.Views["lost"] = new ViewSpec { Name = "Lost", Level = "a.x", Groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal) { ["g"] = new GroupSpec { Name = "G", Leaves = ["a.x"] } } };
        var facts = Run(map);
        Assert.Contains(facts.Violations, v => v.Message.Contains("\"b\" is not a leaf under a"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("leaf \"a.x\" appears in more than one group"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("leaf \"a.y\" under a appears in no group"));
        Assert.Contains(facts.Violations, v => v.Message.Contains("level \"a.x\" is not root or a container"));
    }

    [Fact]
    public void ExplainListsTheTypePairsBehindAnEdge()
    {
        var facts = Run(analysis: TestData.Analysis([new RawDependency(TestData.Helper, TestData.Result, "references", "Shared.Result", TestData.X2, 4)]));
        var pairs = Explain.Pairs(facts, "a", "shared");
        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.Equal(1, p.Count));
        Assert.Equal((TestData.Thing, "Ext.Sdk", 1), Assert.Single(Explain.Pairs(facts, "a", "ext")));
        Assert.Equal((TestData.Thing, TestData.Helper, 1), Assert.Single(Explain.Pairs(facts, "a.x", "a.x")));
        Assert.Empty(Explain.Pairs(facts, "shared", "a"));
    }

    [Fact]
    public void FactsRoundTripThroughJson()
    {
        var map = TestData.Map();
        map.Views["v"] = new ViewSpec { Name = "V", Level = Identifiers.Root, Groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal) { ["all"] = new GroupSpec { Name = "All", Leaves = ["a.x", "a.y", "b", "shared"] } } };
        var facts = Run(map);
        var json = FactsJson.Serialize(facts);
        var back = FactsJson.Deserialize<Facts>(json);
        Assert.Equal(facts.Dependencies.Count, back.Dependencies.Count);
        Assert.Equal(facts.LeafEdges.Count, back.LeafEdges.Count);
        Assert.Equal(facts.Nodes["a"], back.Nodes["a"]);
        Assert.Equal(facts.Levels["root"].Edges.Count, back.Levels["root"].Edges.Count);
        Assert.True(back.Map.Nodes["a.x"].IsLeaf);
        Assert.Equal(["a.x", "a.y", "b", "shared"], back.Map.Views["v"].Groups["all"].Leaves);
    }
}
