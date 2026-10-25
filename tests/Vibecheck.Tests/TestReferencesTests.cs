using Vibecheck.Checking;
using Vibecheck.Diffing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the tests of a decision, as <c>docs/tests.md</c> section 5 describes them.</summary>
public class TestReferencesTests
{
    private const string System = """
        namespace A
        {
            public class Session
            {
                public bool Require() => new Token().Issue() != "";
                public void Close() { }
            }
            public class Token { public string Issue() => ""; }
        }
        """;

    private const string Builder = """
        namespace A.Tests
        {
            public static class SessionBuilder
            {
                public static A.Session Expired() => Make();
                static A.Session Make() => Loop(new A.Session());
                static A.Session Loop(A.Session s)
                {
                    if (s == null) return Expired();
                    s.Close();
                    return s;
                }
            }
        }
        """;

    private const string Tests = """
        namespace A.Tests
        {
            public class SessionTests
            {
                [Xunit.Fact]
                public void Require_Succeeds()
                {
                    Xunit.Assert.True(new A.Session().Require());
                }

                [Xunit.Fact]
                public void Require_Fails_WhenExpired()
                {
                    Xunit.Assert.True(SessionBuilder.Expired().Require());
                }
            }
        }
        """;

    private const string Method = "A.Tests.SessionTests.";

    private static Dictionary<string, string> Files(string system, string builder, string tests) => new()
    {
        ["src/System.cs"] = system,
        ["tests/SessionBuilder.cs"] = builder,
        ["tests/SessionTests.cs"] = tests,
    };

    [Fact]
    public void CallsJoinMembersOfTestTypes()
    {
        var facts = Snippets.Facts(Files(System, Builder, Tests));
        Assert.Contains(new CallFact("A.Tests.SessionBuilder.Expired", "A.Tests.SessionBuilder.Make", "tests/SessionBuilder.cs", 5), facts.Tests.Calls);
        Assert.Contains(facts.Tests.Calls, c => c.From == Method + "Require_Fails_WhenExpired" && c.To == "A.Tests.SessionBuilder.Expired");
        Assert.DoesNotContain(facts.Tests.Calls, c => c.To.StartsWith("A.Session", StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceThroughAChainOfHelpersNamesTheHelperTheTestCalls()
    {
        var references = TestReferences.Of(Snippets.Facts(Files(System, Builder, Tests)).Tests);
        var expired = references[Method + "Require_Fails_WhenExpired"];
        Assert.Equal(new TestReference("direct"), expired["A.Session.Require"]);
        // Loop calls Close, two helpers down the chain that starts at Expired, and the chain's cycle back to Expired ends.
        Assert.Equal(new TestReference("helper", "A.Tests.SessionBuilder.Expired"), expired["A.Session.Close"]);
        // Require calls Token.Issue inside the system, which no reference names, so it is not followed.
        Assert.DoesNotContain("A.Token.Issue", expired.Keys);
        Assert.DoesNotContain("A.Session.Close", references[Method + "Require_Succeeds"].Keys);
    }

    [Fact]
    public void ADecisionListsTheTestMethodsThatReferenceItWithTheirWays()
    {
        // Close takes an argument at head, which reshapes it and edits the helper that calls it; Require is rewritten and
        // its test's assertion edited; Lease is new and public, and nothing tests it.
        var after = Files(
            System.Replace("public void Close() { }", "public void Close(int code) { }").Replace("!= \"\"", "!= null"),
            Builder.Replace("s.Close();", "s.Close(0);"),
            Tests.Replace("Xunit.Assert.True(new A.Session().Require());", "Xunit.Assert.Equal(true, new A.Session().Require());"));
        after["src/Lease.cs"] = "namespace A { public class Lease { public void Renew() { } } }";
        var cs = Snippets.Changeset(Files(System, Builder, Tests), after);

        var close = cs.Decisions.Single(d => d.Declarations.Any(x => x.Symbol == "A.Session.Close"));
        Assert.Equal([new DecisionTest(Method + "Require_Fails_WhenExpired", "unchanged", "helper", "A.Tests.SessionBuilder.Expired")], close.Tests);
        // The helper's edit follows from the decision, and the path counts it apart.
        Assert.Equal([("tests/SessionBuilder.cs", 0)], close.Consequences.Select(c => (c.File, c.Hunk)));
        Assert.Contains("tests/SessionBuilder.cs", close.Marks);
        Assert.Contains("1 consequence in tests", cs.Path.Single(s => s.Target == close.Id).Signal);
        Assert.Equal("1 test file of consequences", cs.Path.Single(s => s.Id == "aside:consequences").Signal);

        var renew = cs.Decisions.Single(d => d.File == "src/Lease.cs");
        Assert.True(renew.Public);
        Assert.Empty(renew.Tests);

        // A rewritten declaration is no decision, but the changed test method that references it names it.
        var method = cs.Tests.Single(t => t.Name == "A.Tests.SessionTests").Methods.Single();
        Assert.Equal((Method + "Require_Succeeds", "changed"), (method.Symbol, method.Status));
        Assert.Equal([new TestedDeclaration("A.Session.Require", "rewritten", "method")], method.Declarations);
    }

    [Fact]
    public void SiteCountsLeaveOutTheTests()
    {
        var after = Files(System.Replace("public void Close() { }", "public void Close(int code) { }"), Builder.Replace("s.Close();", "s.Close(0);"), Tests);
        var cs = Snippets.Changeset(Files(System, Builder, Tests), after);
        var close = cs.Decisions.Single().Declarations.Single();
        Assert.Equal((0, 0), (close.SitesBase, close.SitesHead));
    }
}
