using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the glossary: the names a changeset introduces, with their uses and first use. See <c>docs/attention.md</c> section 8.</summary>
public class GlossaryTests
{
    private const string Clock = "namespace A;\n\n/// <summary>Tells the time.</summary>\npublic class Clock\n{\n    public int Now(int zone) => zone;\n}\n";
    private const string Caller = "namespace C;\n\npublic class Caller\n{\n    public int First(A.Clock clock) => clock.Now(1);\n}\n";

    /// <summary>Clock's <c>Now</c> is renamed to <c>Current</c>, a documented <c>Later</c> is added, and an undocumented <c>Timer</c> appears in B with a field; Caller uses them all.</summary>
    private static Changeset Changeset()
    {
        var before = new Dictionary<string, string> { ["src/Clock.cs"] = Clock, ["src/Caller.cs"] = Caller };
        var after = new Dictionary<string, string>
        {
            ["src/Clock.cs"] = Clock.Replace("    public int Now(int zone) => zone;", "    public int Current(int zone) => zone;\n\n    /// <summary>Adds one.</summary>\n    public int Later() => Current(0) + 1;"),
            ["src/Caller.cs"] = Caller.Replace("clock.Now(1);", "clock.Current(1);\n\n    public int Second(A.Clock clock) => clock.Later() + new B.Timer().Ticks;"),
            ["src/Timer.cs"] = "namespace B;\n\npublic class Timer\n{\n    public int Ticks;\n}\n",
        };
        return Snippets.Changeset(before, after);
    }

    [Fact]
    public void ListsEachAddedOrRenamedNameOnceWithItsUsesAndFirstUse()
    {
        var cs = Changeset();

        // Grouped by block, the most used first within each; constructors are not terms.
        Assert.Equal(["A.Clock.Current", "A.Clock.Later", "B.Timer", "B.Timer.Ticks", "C.Caller.Second"], cs.Glossary.Select(t => t.Symbol));
        Assert.Equal(["a", "a", "b", "b", "c"], cs.Glossary.Select(t => t.Block));

        var current = cs.Glossary[0];
        Assert.Equal(("term:A.Clock.Current", "Clock.Current", "A.Clock.Now", "method", "public", true), (current.Key, current.Name, current.Previous, current.Kind, current.Accessibility, current.Exposed));
        Assert.Equal(("src/Clock.cs", 6, (string?)null), (current.File, current.Line, current.Summary));
        // Later's call inside Clock and Caller's call both use it, and the first use outside Clock is Caller's.
        Assert.Equal(2, current.Uses);
        Assert.Equal(new SiteRef("src/Caller.cs", 5), current.FirstUse);
        Assert.Equal("src/Clock.cs#0", current.Decision);

        var later = cs.Glossary[1];
        Assert.Equal(("Adds one.", 1, new SiteRef("src/Caller.cs", 7)), (later.Summary, later.Uses, later.FirstUse));
        Assert.Null(later.Previous);

        var timer = cs.Glossary[2];
        Assert.Equal(("Timer", "type", null, 1), (timer.Name, timer.Kind, timer.Summary, timer.Uses));
        Assert.Equal(("src/Timer.cs", 3), (timer.File, timer.Line));
        Assert.Equal(new SiteRef("src/Caller.cs", 7), timer.FirstUse);
        Assert.Equal("src/Timer.cs#0", timer.Decision);

        var second = cs.Glossary[4];
        Assert.Equal((0, (SiteRef?)null, "Caller.Second"), (second.Uses, second.FirstUse, second.Name));
    }

    [Fact]
    public void ARenamedTypeIsOneTermAndItsMembersAreNotListedAgain()
    {
        const string old = "namespace A;\n\npublic class Old\n{\n    public int M() => 1;\n}\n";
        var cs = Snippets.Changeset(new Dictionary<string, string> { ["src/T.cs"] = old }, new Dictionary<string, string> { ["src/T.cs"] = old.Replace("Old", "Fresh") });

        var term = Assert.Single(cs.Glossary);
        Assert.Equal(("term:A.Fresh", "Fresh", "A.Old", "type"), (term.Key, term.Name, term.Previous, term.Kind));
        Assert.Null(term.Summary);
    }

    [Fact]
    public void ListsPartsAddedToTheMapAndAssembliesReferencedForTheFirstTime()
    {
        var baseMap = TestData.Map();
        var headMap = TestData.Map();
        headMap.Nodes["c"] = new NodeSpec { Name = "C", Purpose = "The new part.", Bind = ["C.**"] };
        headMap.Externals["json"] = new ExternalSpec { Name = "Json", Purpose = "Serialization.", Bind = ["Newtonsoft.*"] };
        headMap.Views["by-role"] = new ViewSpec { Name = "By role", Purpose = "Roles.", Level = Identifiers.Root, Groups = new(StringComparer.Ordinal) { ["all"] = new GroupSpec { Name = "All", Leaves = ["a.x", "a.y", "b", "shared", "c"] } } };
        var head = TestData.Analysis(extraExternals:
        [
            new ExternalReference(TestData.Service, "Newtonsoft.Json", "Newtonsoft.Json.JsonConvert", TestData.B1, 9),
            new ExternalReference(TestData.Thing, "Newtonsoft.Json", "Newtonsoft.Json.JsonConvert", TestData.X1, 30),
        ]);
        var cs = ChangesetBuilder.Build(Checker.Run(baseMap, TestData.Analysis()), Checker.Run(headMap, head));

        // The map's parts and the assemblies have no block, so they come after the blocks, the most used first.
        Assert.Equal(["term:node:c", "term:external:json", "term:assembly:Newtonsoft.Json", "term:view:by-role"], cs.Glossary.Select(t => t.Key));
        var node = cs.Glossary[0];
        Assert.Equal(("C", Term.Node, "c", "The new part."), (node.Name, node.Kind, node.Block, node.Summary));
        var assembly = cs.Glossary[2];
        Assert.Equal((Term.Assembly, (string?)null, 2, new SiteRef(TestData.X1, 30)), (assembly.Kind, assembly.Block, assembly.Uses, assembly.FirstUse));
        var external = cs.Glossary[1];
        Assert.Equal(("Json", "Serialization.", 2), (external.Name, external.Summary, external.Uses));
        Assert.Equal(("By role", "Roles."), (cs.Glossary[3].Name, cs.Glossary[3].Summary));
        // The assembly Ext.Sdk is referenced at both ends, so it is not new.
        Assert.DoesNotContain(cs.Glossary, t => t.Symbol == "Ext.Sdk");
    }

    [Fact]
    public void TheGlossaryRoundTripsThroughJson()
    {
        var cs = Changeset();
        var back = FactsJson.Deserialize<Changeset>(FactsJson.Serialize(cs));
        Assert.Equal(FactsJson.Serialize(cs.Glossary), FactsJson.Serialize(back.Glossary));
        Assert.Equal(cs.Glossary.Count, back.Glossary.Count);
    }
}
