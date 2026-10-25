using Vibecheck.Diffing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of test files and test types in the changeset and the reading path, as <c>docs/tests.md</c> section 7 describes them.</summary>
public class TestChangesTests
{
    private const string System = """
        namespace A
        {
            public class Session
            {
                public bool Require() => true;
            }
        }
        namespace B
        {
            public class Invoice { public int Total() => 0; }
        }
        """;

    private const string SessionTests = """
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
                public void Require_Twice()
                {
                    Xunit.Assert.True(new A.Session().Require());
                }

                [Xunit.Fact]
                public void Gone() { }
            }
        }
        """;

    private const string SessionTestsAfter = """
        namespace A.Tests
        {
            public class SessionTests
            {
                [Xunit.Fact]
                public void Require_Succeeds()
                {
                    Xunit.Assert.Equal(true, new A.Session().Require());
                }

                [Xunit.Fact]
                public void Require_Twice()
                {
                    Xunit.Assert.True(new A.Session().Require());
                }

                [Xunit.Fact]
                public void Added() { }
            }
        }
        """;

    private const string Builder = """
        namespace A.Tests
        {
            public class SessionBuilder { public A.Session Build() => new A.Session(); }
        }
        """;

    private static Dictionary<string, string> Files(string sessionTests, string builder) => new()
    {
        ["src/System.cs"] = System,
        ["tests/SessionTests.cs"] = sessionTests,
        ["tests/SessionBuilder.cs"] = builder,
    };

    private static Changeset TestsOnly() =>
        Snippets.Changeset(Files(SessionTests, Builder), Files(SessionTestsAfter, Builder.Replace("Build()", "Make()")));

    [Fact]
    public void TestFilesAreMarkedWithTheirSubjectLeafAndHaveNoLeaves()
    {
        var cs = TestsOnly();
        var tests = cs.Files.Single(f => f.Path == "tests/SessionTests.cs");
        Assert.True(tests.Test);
        Assert.Equal("a", tests.Subject);
        Assert.Empty(tests.LeavesHead);
        var builder = cs.Files.Single(f => f.Path == "tests/SessionBuilder.cs");
        Assert.True(builder.Test);
        Assert.Null(builder.Subject);
        Assert.False(cs.Files.Single(f => f.Path == "src/System.cs").Test);
    }

    [Fact]
    public void TheContentCountsChangedTestFilesApartBySubject()
    {
        var cs = TestsOnly();
        Assert.Equal((1, 1, "unchanged"), (cs.Content["a"].Files, cs.Content["a"].Tests, cs.Content["a"].Status));
        Assert.Equal(0, cs.Content["b"].Tests);
        // The top level counts the test infrastructure too.
        Assert.Equal(2, cs.Content["root"].Tests);
    }

    [Fact]
    public void ChangedTestTypesListTheirAddedRemovedAndChangedMethods()
    {
        var cs = TestsOnly();
        Assert.Equal(["A.Tests.SessionBuilder", "A.Tests.SessionTests"], cs.Tests.Select(t => t.Name));
        var tests = cs.Tests.Single(t => t.Name == "A.Tests.SessionTests");
        Assert.Equal("modified", tests.Status);
        Assert.Equal(("A.Session", "a", "name"), (tests.Subject!.Type, tests.Subject.Leaf, tests.Subject.By));
        Assert.Equal(["tests/SessionTests.cs"], tests.Files);
        Assert.Equal(
            [("A.Tests.SessionTests.Added", "added"), ("A.Tests.SessionTests.Gone", "removed"), ("A.Tests.SessionTests.Require_Succeeds", "changed")],
            tests.Methods.Select(m => (m.Symbol, m.Status)));
        var builder = cs.Tests.Single(t => t.Name == "A.Tests.SessionBuilder");
        Assert.Null(builder.Subject);
        Assert.Empty(builder.Methods);
    }

    [Fact]
    public void ATestTypeWhoseFilesDidNotChangeIsNotListed()
    {
        var cs = Snippets.Changeset(Files(SessionTests, Builder), Files(SessionTests, Builder.Replace("Build()", "Make()")));
        Assert.Equal(["A.Tests.SessionBuilder"], cs.Tests.Select(t => t.Name));
    }

    [Fact]
    public void ALeafStepListsItsTestFilesAndTestInfrastructureComesLast()
    {
        var before = Files(SessionTests, Builder);
        var after = Files(SessionTestsAfter, Builder.Replace("Build()", "Make()"));
        after["src/System.cs"] = System.Replace("public bool Require() => true;", "public bool Require() => false;");
        after["README.md"] = "notes";
        var cs = Snippets.Changeset(before, after);
        var leaf = cs.Path.Single(s => s.Target == "a" && s.Kind == "node");
        Assert.Equal(["src/System.cs", "tests/SessionTests.cs"], leaf.Files);
        Assert.Contains("1 test file", leaf.Signal);
        var last = cs.Path.Where(s => s.Kind == "file" && s.Under is null).ToList();
        Assert.Equal(["tests/SessionBuilder.cs", "README.md"], last.Select(s => s.Target));
        Assert.Contains("test infrastructure", last[0].Signal);
        Assert.Contains("outside the map", last[1].Signal);
    }

    [Fact]
    public void AChangeOfTestsAloneGivesTheirLeavesSteps()
    {
        var cs = TestsOnly();
        var leaf = cs.Path.Single(s => s.Target == "a" && s.Kind == "node");
        Assert.Equal(["tests/SessionTests.cs"], leaf.Files);
        Assert.Equal("A: 1 test file", leaf.Signal);
        Assert.Empty(cs.Decisions);
    }
}
