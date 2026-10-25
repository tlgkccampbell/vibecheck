using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of test signals and the path's test steps, as <c>docs/tests.md</c> sections 6 and 7.2 describe them.</summary>
public class TestSignalsTests
{
    private const string System = """
        namespace A
        {
            public class Session
            {
                public bool Require() => true;
                public int Count() => 1;
            }
            public class Token { public int Issue() => 1; }
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
                    Xunit.Assert.Equal(1, new A.Session().Count());
                }

                [Xunit.Fact]
                public void Count_IsOne()
                {
                    Xunit.Assert.Equal(1, new A.Session().Count());
                }

                [Xunit.Fact]
                public void Old_Name()
                {
                    Xunit.Assert.True(true);
                }
            }

            public class TokenTests
            {
                [Xunit.Fact]
                public void Issue_IsOne()
                {
                    Xunit.Assert.Equal(1, new A.Token().Issue());
                }

                [Xunit.Fact]
                public void Issue_Twice()
                {
                    var token = new A.Token();
                    Xunit.Assert.Equal(1, token.Issue() + token.Issue() - 1);
                }

                [Xunit.Fact]
                public void Issue_IsPositive()
                {
                    Xunit.Assert.True(new A.Token().Issue() > 0);
                }
            }
        }
        """;

    /// <summary>
    /// Returns the changeset in which Require is rewritten and the tests change: Require_Succeeds loses an assertion,
    /// Count_IsOne's expected value changes, Old_Name is renamed unchanged, Issue_IsOne is skipped, Issue_Twice is
    /// removed and Issue_IsPositive's assertion changes in more than its literals.
    /// </summary>
    private static Changeset Changed()
    {
        var after = Tests
            .Replace("""
                        Xunit.Assert.True(new A.Session().Require());
                        Xunit.Assert.Equal(1, new A.Session().Count());
            """, """
                        Xunit.Assert.True(new A.Session().Require());
            """)
            .Replace("Xunit.Assert.Equal(1, new A.Session().Count());", "Xunit.Assert.Equal(2, new A.Session().Count());")
            .Replace("Old_Name", "New_Name")
            .Replace("""
                    [Xunit.Fact]
                    public void Issue_IsOne()
            """, """
                    [Xunit.Fact(Skip = "flaky")]
                    public void Issue_IsOne()
            """)
            .Replace("""
                    [Xunit.Fact]
                    public void Issue_Twice()
                    {
                        var token = new A.Token();
                        Xunit.Assert.Equal(1, token.Issue() + token.Issue() - 1);
                    }

            """, "")
            .Replace("Xunit.Assert.True(new A.Token().Issue() > 0);", "Xunit.Assert.NotNull(new A.Token().Issue());");
        return Snippets.Changeset(
            new Dictionary<string, string> { ["src/System.cs"] = System, ["tests/Tests.cs"] = Tests },
            new Dictionary<string, string> { ["src/System.cs"] = System.Replace("=> true;", "=> !false;"), ["tests/Tests.cs"] = after });
    }

    private static List<TestSignal> Signals(Changeset cs) => cs.Hunks.SelectMany(f => f.Hunks.SelectMany(h => h.Signals ?? [])).ToList();

    [Fact]
    public void TestMethodsRecordSkipsAndAssertions()
    {
        var facts = Snippets.Facts(new Dictionary<string, string>
        {
            ["src/System.cs"] = System,
            ["tests/Tests.cs"] = """
                namespace A.Tests
                {
                    public class Checks
                    {
                        [Xunit.Fact(Skip = "later")]
                        public void Skipped() { }

                        [NUnit.Framework.Test, NUnit.Framework.Ignore("later")]
                        public void Ignored() { }

                        [Xunit.Fact]
                        public void Counted()
                        {
                            NUnit.Framework.Assert.That(new A.Session().Count(), NUnit.Framework.Is.EqualTo(1));
                            Xunit.Assert.All(new[] { true }, b =>
                                Xunit.Assert.True(b));
                        }
                    }
                }
                """,
        });
        var methods = facts.Tests.Types.Single().Methods.ToDictionary(m => m.Symbol.Split('.')[^1]);
        Assert.True(methods["Skipped"].Skipped);
        Assert.True(methods["Ignored"].Skipped);
        Assert.False(methods["Counted"].Skipped);
        // The constraint inside Assert.That is not an assertion of its own, and the one inside the lambda is.
        Assert.Equal([new LineRange(14, 14), new LineRange(15, 16), new LineRange(16, 16)], methods["Counted"].Assertions);
    }

    [Fact]
    public void EachKindOfSignalIsFound()
    {
        var signals = Signals(Changed()).Select(s => (s.Kind, Method: s.Method.Split('.')[^1])).ToList();
        Assert.Equal(
        [
            (TestSignal.AssertionRemoved, "Require_Succeeds"),
            (TestSignal.AssertionChanged, "Require_Succeeds"),
            (TestSignal.ExpectedChanged, "Count_IsOne"),
            (TestSignal.Skipped, "Issue_IsOne"),
            (TestSignal.AssertionChanged, "Issue_IsPositive"),
            (TestSignal.Removed, "Issue_Twice"),
        ], signals.OrderBy(s => s.Method.StartsWith("Issue")).ThenBy(s => s.Method == "Count_IsOne").ToList());
        // A test method renamed without changes is not removed.
        Assert.DoesNotContain(signals, s => s.Method == "Old_Name");
    }

    [Fact]
    public void ASignalTouchesChangedCodeExactlyWhenItsMethodReferencesAChangedDeclaration()
    {
        var signals = Signals(Changed());
        Assert.All(signals.Where(s => s.Method.EndsWith("Require_Succeeds")), s => Assert.Equal([new TestedDeclaration("A.Session.Require", "rewritten", "method")], s.Touches));
        Assert.All(signals.Where(s => !s.Method.EndsWith("Require_Succeeds")), s => Assert.Empty(s.Touches));
        var methods = Changed().Tests.SelectMany(t => t.Methods).ToDictionary(m => m.Symbol.Split('.')[^1]);
        Assert.Equal([TestSignal.Skipped], methods["Issue_IsOne"].Signals);
    }

    [Fact]
    public void ATestStepFollowsTheDecisionsAndTheOtherSignalsCloseTheFirstTier()
    {
        var first = Changed().Path.Where(s => s.Tier == "first").ToList();
        Assert.Equal(["test", "tests"], first.Select(s => s.Kind));
        Assert.Equal("A.Tests.SessionTests", first[0].Target);
        Assert.Equal("assertion removed from SessionTests.Require_Succeeds, which references Session.Require, rewritten; "
            + "assertion changed in SessionTests.Require_Succeeds, which references Session.Require, rewritten", first[0].Signal);
        Assert.Equal(["tests/Tests.cs"], first[0].Files);
        Assert.Equal("4 tests changed in ways no changed declaration they reference explains", first[1].Signal);
        Assert.Equal(["SessionTests.Count_IsOne: expected value changed", "TokenTests.Issue_IsOne: skipped", "TokenTests.Issue_IsPositive: assertion changed", "TokenTests.Issue_Twice: removed"],
            first[1].Members!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TestStepsAreOrderedByTheReachOfTheDecisionsTheyTouchAndNeedNoClosingStep()
    {
        var before = new Dictionary<string, string>
        {
            ["src/Wide.cs"] = "namespace A { public class Wide { public void Go() { } } }",
            ["src/Narrow.cs"] = "namespace A { public class Narrow { public void Go() { } } }",
            ["src/B.cs"] = "namespace B { public class User { public void Use() { new A.Wide().Go(); } } }",
            ["src/C.cs"] = "namespace C { public class User { public void Use() { new A.Wide().Go(); } } }",
            ["tests/Tests.cs"] = """
                namespace A.Tests
                {
                    public class NarrowTests
                    {
                        [Xunit.Fact]
                        public void Go()
                        {
                            new A.Narrow().Go();
                            Xunit.Assert.True(true);
                        }
                    }

                    public class WideTests
                    {
                        [Xunit.Fact]
                        public void Go()
                        {
                            new A.Wide().Go();
                            Xunit.Assert.True(true);
                        }
                    }
                }
                """,
        };
        var after = before.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("Go() { }", "Go(int n) { }").Replace(".Go()", ".Go(0)").Replace("True(true)", "True(false)"));
        var cs = Snippets.Changeset(before, after);
        var tests = cs.Path.Where(s => s.Kind is "test" or "tests").ToList();
        Assert.Equal(["A.Tests.WideTests", "A.Tests.NarrowTests"], tests.Select(s => s.Target));
        Assert.Contains("expected value changed in WideTests.Go, which references Wide.Go, signature changed", tests[0].Signal);
        Assert.True(cs.Path.FindIndex(s => s.Kind == "decision") < cs.Path.FindIndex(s => s.Kind == "test"));
    }

    [Theory]
    [InlineData("Assert.Equal(1, x);", "Assert.Equal(2, x);", true)]
    [InlineData("Assert.Equal(\"a\", x);", "Assert.Equal(\"b\", x);", true)]
    [InlineData("Assert.Equal(1, x);", "Assert.NotEqual(1, x);", false)]
    [InlineData("Assert.Equal(1, x);", "Assert.Equal(1, x);", false)]
    [InlineData("Assert.Equal(1, x);", "Assert.Equal(1, y);", false)]
    public void ALinePairDiffersOnlyInLiterals(string a, string b, bool expected) =>
        Assert.Equal(expected, TestSignals.LiteralsOnly(a, b));
}
