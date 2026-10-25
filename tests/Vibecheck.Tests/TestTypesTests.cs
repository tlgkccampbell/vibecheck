using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Format;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of test types in the map and the facts, and of their subjects, as <c>docs/tests.md</c> sections 3 and 4 describe them.</summary>
public class TestTypesTests
{
    private const string System = """
        namespace A.Auth
        {
            public class Session
            {
                public bool Require() => true;
                public void Close() { }
            }
            public class Token { public string Issue() => ""; }
        }
        namespace B.Billing
        {
            public class Session { public void Open() { } }
            public class Invoice { public int Total() => 0; }
        }
        """;

    private static Facts Check(string tests, ArchitectureMap? map = null) =>
        Checker.Run(map ?? Snippets.Map(), Snippets.Analyze(new Dictionary<string, string> { ["src/System.cs"] = System, ["tests/Tests.cs"] = tests }));

    private static TestTypeFact Type(Facts facts, string name) => facts.Tests.Types.Single(t => t.Name == name);

    [Fact]
    public void ATestTypeBindsToNothingAndCreatesNoEdgeButIsKept()
    {
        var facts = Check("""
            namespace A.Auth.Tests
            {
                public class SessionTests
                {
                    [Xunit.Fact]
                    public void Require_Succeeds() { Xunit.Assert.True(new A.Auth.Session().Require()); }
                }
            }
            """);
        Assert.Empty(facts.Violations);
        Assert.Empty(facts.Warnings);
        Assert.DoesNotContain(facts.Types, t => t.Name.Contains("Tests"));
        Assert.DoesNotContain(facts.Dependencies, d => d.From.Contains("Tests"));
        Assert.DoesNotContain(facts.LeafEdges, e => e.From == ArchitectureMap.Unsorted);
        Assert.DoesNotContain(facts.ExternalRefs, e => e.From.Contains("Tests"));
        Assert.DoesNotContain(facts.Declarations, d => d.Type.Contains("Tests"));
        Assert.Equal(new ExcludedCounts(0, 0), facts.Excluded);
        var type = Type(facts, "A.Auth.Tests.SessionTests");
        Assert.Equal("Snippets.Tests", type.Assembly);
        Assert.Equal(["tests/Tests.cs"], type.Files);
        var file = facts.Files.Single(f => f.Path == "tests/Tests.cs");
        Assert.True(file.Test);
        Assert.Empty(file.Leaves);
        Assert.Equal("a", file.Subject);
        Assert.False(facts.Files.Single(f => f.Path == "src/System.cs").Test);
    }

    [Fact]
    public void ATypeBothListsMatchIsExcluded()
    {
        var map = Snippets.Map();
        map.Exclude.Add("Snippets.Tests::**");
        var facts = Check("namespace A.Auth.Tests { public class SessionTests { [Xunit.Fact] public void F() { } } }", map);
        Assert.Empty(facts.Tests.Types);
        Assert.DoesNotContain(facts.Files, f => f.Path == "tests/Tests.cs");
        Assert.Equal(new ExcludedCounts(1, 1), facts.Excluded);
    }

    [Fact]
    public void AnExcludePatternThatMatchesOnlyTestProjectsEarnsAWarning()
    {
        var map = new ArchitectureMap { Version = 0, Solution = "Snippets.sln", Exclude = ["System.**", "Snippets.Tests::**", "A.**"] };
        map.Nodes["b"] = new NodeSpec { Name = "B", Bind = ["B.**"] };
        var facts = Check("namespace A.Auth.Tests { public class SessionTests { [Xunit.Fact] public void F() { } } }", map);
        var warning = Assert.Single(facts.Warnings);
        Assert.Contains("\"Snippets.Tests::**\"", warning);
        Assert.Contains("move it to \"tests\"", warning);
    }

    [Fact]
    public void AnAttributeOnATestTypeEarnsAWarning()
    {
        var map = Snippets.Map();
        var analysis = Snippets.Analyze(new Dictionary<string, string> { ["src/System.cs"] = System, ["tests/Tests.cs"] = "namespace T { public class SessionTests { } }" });
        analysis = analysis with { Types = analysis.Types.Select(t => t.Name == "T.SessionTests" ? t with { Attribute = "a" } : t).ToList() };
        var facts = Checker.Run(map, analysis);
        Assert.Contains(facts.Warnings, w => w.Contains("T.SessionTests") && w.Contains("lists it as a test type"));
    }

    [Fact]
    public void TestMethodsCarryAFrameworkAttribute()
    {
        var facts = Check("""
            namespace A.Auth.Tests
            {
                public class SessionTests
                {
                    [Xunit.Fact]
                    public void Fact() { }

                    [Xunit.Theory, Xunit.InlineData(1)]
                    public void Theory(int n) { }

                    [NUnit.Framework.Test]
                    public void NUnit() { }

                    [Xunit.SkippableFact]
                    public void Skippable() { }

                    public void Helper() { }

                    [System.Obsolete]
                    public void Obsolete() { }
                }
            }
            """);
        var methods = Type(facts, "A.Auth.Tests.SessionTests").Methods;
        Assert.Equal(["Fact", "Theory", "NUnit", "Skippable"], methods.Select(m => m.Symbol.Split('.')[^1]));
        var fact = methods[0];
        Assert.Equal("tests/Tests.cs", fact.File);
        Assert.Equal((5, 6), (fact.First, fact.Last));
    }

    [Fact]
    public void ATestTypeIsPlacedByNameFirst()
    {
        var facts = Check("""
            namespace A.Auth.Tests
            {
                public class TokenTests
                {
                    [Xunit.Fact]
                    public void Issues() { new A.Auth.Session().Require(); new A.Auth.Session().Close(); new A.Auth.Token().Issue(); }
                }
                public class TokenShould { [Xunit.Fact] public void Issue() { } }
                public class TokenSpecs { [Xunit.Fact] public void Issue() { } }
            }
            """);
        foreach (var name in new[] { "TokenTests", "TokenShould", "TokenSpecs" })
            Assert.Equal(new SubjectFact("A.Auth.Token", "a", "name"), Type(facts, "A.Auth.Tests." + name).Subject);
    }

    [Fact]
    public void ANameSharedByTwoSystemTypesIsDecidedByNamespace()
    {
        var facts = Check("""
            namespace A.Auth.Tests { public class SessionTests { [Xunit.Fact] public void F() { } } }
            namespace Tests.Billing { public class SessionTests { [Xunit.Fact] public void F() { } } }
            namespace C.Tests { public class SessionTests { [Xunit.Fact] public void F() { } } }
            """);
        Assert.Equal(new SubjectFact("A.Auth.Session", "a", "name"), Type(facts, "A.Auth.Tests.SessionTests").Subject);
        Assert.Equal(new SubjectFact("B.Billing.Session", "b", "name"), Type(facts, "Tests.Billing.SessionTests").Subject);
        // C shares no trailing segment with either namespace, and the test references nothing, so neither rule places it.
        Assert.Null(Type(facts, "C.Tests.SessionTests").Subject);
    }

    [Fact]
    public void ATestTypeIsPlacedByReferencesWhenItsNamePlacesNothing()
    {
        var facts = Check("""
            namespace C.Tests
            {
                public class Checkout
                {
                    [Xunit.Fact]
                    public void Totals() { var i = new B.Billing.Invoice(); i.Total(); i.Total(); new A.Auth.Token().Issue(); }
                }
                public class Even
                {
                    [Xunit.Fact]
                    public void Splits() { new B.Billing.Invoice(); new A.Auth.Token(); }
                }
            }
            """);
        Assert.Equal(new SubjectFact("B.Billing.Invoice", "b", "references"), Type(facts, "C.Tests.Checkout").Subject);
        // Two types referenced equally give no single most-referenced type.
        Assert.Null(Type(facts, "C.Tests.Even").Subject);
    }

    [Fact]
    public void ATestTypeWithoutTestMethodsIsTestInfrastructure()
    {
        var facts = Check("""
            namespace A.Auth.Tests
            {
                public class SessionBuilder { public A.Auth.Session Expired() => new A.Auth.Session(); }
                public class SessionTests { [Xunit.Fact] public void F() { new SessionBuilder().Expired(); } }
            }
            """);
        Assert.Null(Type(facts, "A.Auth.Tests.SessionBuilder").Subject);
        Assert.Empty(Type(facts, "A.Auth.Tests.SessionBuilder").Methods);
        Assert.Equal("2 test types: 1 by name, 0 by references, 1 infrastructure", TestTypes.Placement(facts.Tests));
    }

    [Fact]
    public void ExercisesNameTheMemberThatHoldsThem()
    {
        var facts = Check("""
            namespace A.Auth.Tests
            {
                public class SessionTests
                {
                    private readonly A.Auth.Token token = new A.Auth.Token();

                    [Xunit.Fact]
                    public void Require_Succeeds()
                    {
                        Xunit.Assert.True(new A.Auth.Session().Require());
                    }
                }
            }
            """);
        var require = Assert.Single(facts.Tests.Exercises, e => e.Symbol == "A.Auth.Session.Require");
        Assert.Equal(new ExerciseFact("A.Auth.Tests.SessionTests", "A.Auth.Tests.SessionTests.Require_Succeeds", "A.Auth.Session.Require", "tests/Tests.cs", 10), require);
        Assert.Contains(facts.Tests.Exercises, e => e.Symbol == "A.Auth.Token" && e.Method == "A.Auth.Tests.SessionTests.token");
        Assert.DoesNotContain(facts.Tests.Exercises, e => e.Symbol.StartsWith("Xunit.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTestsListRoundTripsAndIsWrittenOnlyWhenItHoldsAPattern()
    {
        var map = TestData.Map();
        map.Tests.Add("Example.Tests::**");
        var text = MapWriter.Write(map);
        Assert.Contains("  \"exclude\": [],\n  \"tests\": [\"Example.Tests::**\"],\n  \"nodes\": {", text);
        Assert.Equal(["Example.Tests::**"], MapReader.Read(text).Tests);
        Assert.DoesNotContain("\"tests\"", MapWriter.Write(TestData.Map()));
        var malformed = TestData.Map();
        malformed.Tests.Add("Example.***");
        Assert.StartsWith("tests:", Assert.Single(Checker.Run(malformed, TestData.Analysis()).Violations).Message);
    }
}
