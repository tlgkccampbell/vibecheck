using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of declarations and sites, the declaration diff, and decisions and their consequences. See <c>docs/attention.md</c> sections 3 and 4.</summary>
public class ClusteringTests
{
    private const string Clock = """
        namespace A;

        public class Clock
        {
            public int Now(int zone) => zone;

            public int Later(int zone) => Now(zone) + 1;
        }
        """;

    private const string Caller = """
        namespace B;

        public class Caller
        {
            public int First(A.Clock clock) => clock.Now(1);

            public int Second(A.Clock clock)
            {
                var x = 2;
                return clock.Now(x);
            }
        }
        """;

    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);

    private static Declaration Declared(AnalysisResult analysis, string symbol) => analysis.Declarations.Single(d => d.Symbol == symbol);

    [Fact]
    public void DeclarationsCoverTypesAndMembersWithTheirLines()
    {
        var analysis = Snippets.Analyze(Files(("src/Clock.cs", Clock)));
        var type = Declared(analysis, "A.Clock");
        Assert.Equal(("type", "A.Clock", (string?)null, 3, 8, 4), (type.Kind, type.Type, type.Container, type.FirstLine, type.LastLine, type.BodyLine));
        var now = Declared(analysis, "A.Clock.Now");
        Assert.Equal(("method", "A.Clock", "public", true, 5, 5), (now.Kind, now.Container, now.Accessibility, now.Exposed, now.FirstLine, now.BodyLine));
        Assert.Equal("Now", now.Name);
        Assert.NotNull(now.Body);
    }

    [Fact]
    public void SitesRecordReferencesWithinATypeAndConstructors()
    {
        var analysis = Snippets.Analyze(Files(("src/Clock.cs", Clock), ("src/Maker.cs", "namespace B;\nclass Maker\n{\n    A.Clock Make() => new A.Clock();\n}\n")));
        // A call within the type is a site rather than a dependency.
        Assert.Contains(analysis.Sites, s => s.Symbol == "A.Clock.Now" && s.FromType == "A.Clock" && s.Line == 7);
        Assert.DoesNotContain(analysis.Dependencies, d => d.FromType == d.ToType);
        // The implicit constructor has no declaration, so only a declared one is a site.
        Assert.DoesNotContain(analysis.Sites, s => s.FromType == "B.Maker");
        var declared = Snippets.Analyze(Files(("src/Clock.cs", "namespace A;\npublic class Clock\n{\n    public Clock() { }\n}\n"), ("src/Maker.cs", "namespace B;\nclass Maker\n{\n    A.Clock Make() => new A.Clock();\n}\n")));
        Assert.Contains(declared.Sites, s => s.Symbol == "A.Clock.Clock" && s.FromType == "B.Maker" && s.ToType == "A.Clock" && s.Line == 4);
    }

    [Fact]
    public void TheDiffTellsEachKindOfChangeApart()
    {
        const string before = """
            namespace A;
            class Keep { int Same() => 1; int Body() => 1; int Shape(int a) => a; int Old() => 7; void Gone() { } }
            class Other { int Travel() => 3; }
            """;
        const string after = """
            namespace A;
            class Keep { int Same() => 1; int Body() => 2; int Shape(long a) => 0; int New() => 7; void Fresh() { Same(); } }
            class Third { int Travel() => 3; }
            class Other { }
            """;
        var changes = DeclarationDiff.Diff(Snippets.Analyze(Files(("a.cs", before))).Declarations, Snippets.Analyze(Files(("a.cs", after))).Declarations);
        var byChange = changes.ToDictionary(c => c.Symbol, c => (c.Change, c.Previous));
        Assert.Equal(("rewritten", null), byChange["A.Keep.Body"]);
        Assert.Equal(("reshaped", null), byChange["A.Keep.Shape"]);
        Assert.Equal(("renamed", "A.Keep.Old"), byChange["A.Keep.New"]);
        Assert.Equal(("removed", null), byChange["A.Keep.Gone"]);
        Assert.Equal(("added", null), byChange["A.Keep.Fresh"]);
        Assert.Equal(("moved", "A.Other.Travel"), byChange["A.Third.Travel"]);
        Assert.Equal(("added", null), byChange["A.Third"]);
        Assert.False(byChange.ContainsKey("A.Keep.Same"));
        Assert.False(byChange.ContainsKey("A.Other"));
    }

    [Fact]
    public void OverloadsAreOneDeclaration()
    {
        static List<DeclarationChange> Diff(string before, string after) =>
            DeclarationDiff.Diff(Snippets.Analyze(Files(("a.cs", before))).Declarations, Snippets.Analyze(Files(("a.cs", after))).Declarations);
        Assert.Equal([new DeclarationChange("A.C.M", "reshaped", "method")],
            Diff("namespace A; class C { void M() { } }", "namespace A; class C { void M() { } void M(int x) { } }"));
        Assert.Equal([new DeclarationChange("A.C.M", "rewritten", "method")],
            Diff("namespace A; class C { void M() { } void M(int x) { } }", "namespace A; class C { void M() { } void M(int x) { x++; } }"));
    }

    [Fact]
    public void ARenamedTypeCarriesItsMembers()
    {
        const string before = "namespace A;\nclass Old\n{\n    Old Self() => new Old();\n    int Count;\n}\n";
        const string after = "namespace A;\nclass New\n{\n    New Self() => new New();\n    int Count;\n}\n";
        var changes = DeclarationDiff.Diff(Snippets.Analyze(Files(("a.cs", before))).Declarations, Snippets.Analyze(Files(("a.cs", after))).Declarations);
        Assert.Equal([("A.New", "A.Old"), ("A.New.Count", "A.Old.Count"), ("A.New.Self", "A.Old.Self")], changes.Select(c => (c.Symbol, c.Previous!)));
        Assert.All(changes, c => Assert.Equal("renamed", c.Change));
    }

    [Fact]
    public void ACallerWhoseLinesAllReferenceAReshapedMethodIsItsConsequence()
    {
        var after = Clock.Replace("Now(int zone) => zone", "Now(long zone) => zone");
        var callerAfter = Caller.Replace("clock.Now(1)", "clock.Now(1L)").Replace("return clock.Now(x);", "return clock.Now((long)x);");
        var cs = Snippets.Changeset(Files(("src/Clock.cs", Clock), ("src/Caller.cs", Caller)), Files(("src/Clock.cs", after), ("src/Caller.cs", callerAfter)));

        var decision = Assert.Single(cs.Decisions);
        Assert.Equal(("src/Clock.cs", 0, true), (decision.File, decision.Hunk, decision.Public));
        var declared = Assert.Single(decision.Declarations);
        Assert.Equal(new DecisionDeclaration("A.Clock.Now", "reshaped", "method", "public", 3, 3), declared);
        var hunks = cs.Hunks.Single(f => f.Path == "src/Caller.cs").Hunks;
        Assert.All(hunks, h => Assert.Equal([decision.Id], h.Follows!));
        Assert.Equal(hunks.Count, decision.Consequences.Count);
        Assert.Equal(["src/Caller.cs"], decision.Marks);
        Assert.Null(cs.Hunks.Single(f => f.Path == "src/Clock.cs").Hunks[0].Follows);
    }

    [Fact]
    public void AnUnexplainedLineMakesAHunkNeither()
    {
        var after = Clock.Replace("Now(int zone) => zone", "Now(long zone) => zone");
        var callerAfter = Caller.Replace("clock.Now(1)", "clock.Now(1L)").Replace("var x = 2;", "var x = 3;").Replace("return clock.Now(x);", "return clock.Now((long)x);");
        var cs = Snippets.Changeset(Files(("src/Clock.cs", Clock), ("src/Caller.cs", Caller)), Files(("src/Clock.cs", after), ("src/Caller.cs", callerAfter)));

        var decision = Assert.Single(cs.Decisions);
        var hunks = cs.Hunks.Single(f => f.Path == "src/Caller.cs").Hunks;
        Assert.Single(hunks);
        Assert.Null(hunks[0].Follows);
        Assert.Empty(decision.Consequences);
        Assert.Empty(decision.Marks);
    }

    [Fact]
    public void AHunkThatChangesADeclarationOfItsOwnIsADecision()
    {
        var after = Clock.Replace("Now(int zone) => zone", "Now(long zone) => zone");
        var callerAfter = Caller.Replace("public int First(A.Clock clock) => clock.Now(1);", "public long First(A.Clock clock) => clock.Now(1L);");
        var cs = Snippets.Changeset(Files(("src/Clock.cs", Clock), ("src/Caller.cs", Caller)), Files(("src/Clock.cs", after), ("src/Caller.cs", callerAfter)));

        Assert.Equal(["src/Caller.cs#0", "src/Clock.cs#0"], cs.Decisions.Select(d => d.Id).Order());
        Assert.Equal("B.Caller.First", cs.Decisions.Single(d => d.File == "src/Caller.cs").Declarations.Single().Symbol);
        Assert.All(cs.Hunks.SelectMany(f => f.Hunks), h => Assert.Null(h.Follows));
    }

    [Fact]
    public void AConsequenceOfTwoDecisionsIsListedUnderBothAndShownUnderTheOneWithMoreSites()
    {
        const string clocks = "namespace A;\npublic class Clock\n{\n    public int Now(int zone) => zone;\n}\n\n\n\n\n\n\n\npublic class Calendar\n{\n    public int Day(int zone) => zone;\n}\n";
        const string caller = "namespace B;\nclass Caller\n{\n    int Both(A.Clock c, A.Calendar d)\n    {\n        var a = c.Now(1);\n        var b = d.Day(1);\n        return c.Now(2) + b;\n    }\n}\n";
        var after = clocks.Replace("Now(int zone)", "Now(long zone)").Replace("Day(int zone)", "Day(long zone)");
        var callerAfter = caller.Replace("c.Now(1)", "c.Now(1L)").Replace("d.Day(1)", "d.Day(1L)").Replace("c.Now(2)", "c.Now(2L)");
        var cs = Snippets.Changeset(Files(("src/Clocks.cs", clocks), ("src/Caller.cs", caller)), Files(("src/Clocks.cs", after), ("src/Caller.cs", callerAfter)));

        Assert.Equal(2, cs.Decisions.Count);
        var now = cs.Decisions.Single(d => d.Declarations.Any(x => x.Symbol == "A.Clock.Now"));
        var day = cs.Decisions.Single(d => d.Declarations.Any(x => x.Symbol == "A.Calendar.Day"));
        var hunk = cs.Hunks.Single(f => f.Path == "src/Caller.cs").Hunks.Single();
        Assert.Equal([now.Id, day.Id], hunk.Follows!);
        Assert.Equal(new Consequence("src/Caller.cs", 0, 4), now.Consequences.Single());
        Assert.Equal(new Consequence("src/Caller.cs", 0, 2), day.Consequences.Single());
        // Each decision explains only some of the hunk's lines, so neither one's mark covers the file.
        Assert.Empty(now.Marks);
        Assert.Empty(day.Marks);
    }

    [Fact]
    public void ARenamedMethodExplainsTheLinesThatUseEitherName()
    {
        var after = Clock.Replace("int Now(int zone)", "int Current(int zone)").Replace("=> Now(zone)", "=> Current(zone)");
        var callerAfter = Caller.Replace("clock.Now", "clock.Current");
        var cs = Snippets.Changeset(Files(("src/Clock.cs", Clock), ("src/Caller.cs", callerAfter.Replace("clock.Current", "clock.Now"))), Files(("src/Clock.cs", after), ("src/Caller.cs", callerAfter)));

        var decision = Assert.Single(cs.Decisions);
        Assert.Equal(new DecisionDeclaration("A.Clock.Current", "renamed", "method", "public", 3, 3, "A.Clock.Now"), decision.Declarations.Single());
        Assert.All(cs.Hunks.Single(f => f.Path == "src/Caller.cs").Hunks, h => Assert.Equal([decision.Id], h.Follows!));
        Assert.Equal(["src/Caller.cs"], decision.Marks);
    }

    [Fact]
    public void ADecisionIsPublicOnlyWhenItsDeclarationCanBeSeenOutsideTheAssembly()
    {
        var internalClock = Clock.Replace("public class Clock", "internal class Clock");
        var cs = Snippets.Changeset(Files(("src/Clock.cs", internalClock)), Files(("src/Clock.cs", internalClock.Replace("Now(int zone)", "Now(long zone)"))));
        Assert.False(Assert.Single(cs.Decisions).Public);
    }

    [Fact]
    public void ATrivialHunkIsNeitherADecisionNorAConsequence()
    {
        var baseFacts = Snippets.Facts(Files(("src/Clock.cs", Clock)));
        var headFacts = Snippets.Facts(Files(("src/Clock.cs", Clock.Replace("Now(int zone)", "Now(long zone)"))));
        var hunk = Snippets.Diff(Clock, Clock.Replace("Now(int zone)", "Now(long zone)")).Single() with { Trivial = true };
        var cs = ChangesetBuilder.Build(baseFacts, headFacts, [new FileHunks("src/Clock.cs", [hunk])]);
        Assert.Empty(cs.Decisions);
    }

    [Fact]
    public void DecisionsRoundTripThroughJson()
    {
        var after = Clock.Replace("Now(int zone) => zone", "Now(long zone) => zone");
        var callerAfter = Caller.Replace("clock.Now(1)", "clock.Now(1L)").Replace("return clock.Now(x);", "return clock.Now((long)x);");
        var cs = Snippets.Changeset(Files(("src/Clock.cs", Clock), ("src/Caller.cs", Caller)), Files(("src/Clock.cs", after), ("src/Caller.cs", callerAfter)));
        var json = FactsJson.Serialize(cs);
        Assert.Contains("\"follows\":[\"src/Clock.cs#0\"]", json);
        var back = FactsJson.Deserialize<Vibecheck.Diffing.Changeset>(json);
        Assert.Equal(cs.Decisions.Single().Marks, back.Decisions.Single().Marks);
        Assert.Equal(cs.Decisions.Single().Declarations, back.Decisions.Single().Declarations);
    }
}
