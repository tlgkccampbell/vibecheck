using Archmap.Analysis;
using Archmap.Bootstrap;
using Archmap.Checking;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

public class SkeletonTests
{
    private static AnalysisResult Projects(params ProjectInfo[] projects) => new()
    {
        Files = projects.SelectMany(p => p.Files).Select(f => new SourceFile(f, "h", "p")).ToList(),
        Dependencies = [],
        ExternalReferences = [],
        Unresolvable = [],
        PartialTypes = [],
        Projects = projects,
    };

    [Fact]
    public void StripsCommonProjectPrefixAndMirrorsDirectories()
    {
        var map = Skeleton.Build(Projects(
            new ProjectInfo("Example.Billing", "src/Example.Billing", ["src/Example.Billing/Core/Payments/Capture.cs", "src/Example.Billing/Core/Service.cs", "src/Example.Billing/Api/Endpoints.cs"]),
            new ProjectInfo("Example.Auth", "src/Example.Auth", ["src/Example.Auth/Session.cs"])), "Example.sln");

        Assert.Equal(["auth", "billing", "billing.api", "billing.core", "billing.core.payments", "billing.core.self"], map.Nodes.Keys);
        Assert.Equal(["src/Example.Auth/**/*.cs"], map.Nodes["auth"].Bind);
        Assert.Equal(["src/Example.Billing/Core/*.cs"], map.Nodes["billing.core.self"].Bind);
        Assert.Equal(["src/Example.Billing/Core/Payments/**/*.cs"], map.Nodes["billing.core.payments"].Bind);
        Assert.False(map.Nodes["billing.core"].IsLeaf);
        Assert.Equal("Example.Billing", map.Nodes["billing"].Name);
        Assert.Equal("Payments", map.Nodes["billing.core.payments"].Name);
        Assert.Null(map.Nodes["billing"].Edges);
        Assert.Equal(ArchMap.Observed, map.Policies.Root.Edges);
        Assert.Empty(map.Edges);
    }

    [Fact]
    public void SingleProjectKeepsItsFullName()
    {
        var map = Skeleton.Build(Projects(new ProjectInfo("Example.Only", "src", ["src/A.cs"])), "x.sln");
        Assert.Equal(["example-only"], map.Nodes.Keys);
    }

    [Fact]
    public void DoesNotCollapseSingleChildChains()
    {
        var map = Skeleton.Build(Projects(new ProjectInfo("P", "src/P", ["src/P/Features/Payments/A.cs"])), "x.sln");
        Assert.Contains("p.features.payments", map.Nodes.Keys);
        Assert.False(map.Nodes["p.features"].IsLeaf);
    }

    [Fact]
    public void CollidingNormalizedNamesGetSuffixes()
    {
        var map = Skeleton.Build(Projects(new ProjectInfo("P", "src/P", ["src/P/Foo/A.cs", "src/P/foo/B.cs"])), "x.sln");
        Assert.Equal(["p", "p.foo", "p.foo-2"], map.Nodes.Keys);
    }

    [Fact]
    public void LinkedFilesOutsideTheProjectDirectoryGetTheirOwnLeaf()
    {
        var map = Skeleton.Build(Projects(new ProjectInfo("P", "src/P", ["src/P/A.cs", "shared/Version.cs"])), "x.sln");
        Assert.Equal(["shared/Version.cs"], map.Nodes["p.linked"].Bind);
        Assert.False(map.Nodes["p"].IsLeaf);
        Assert.Equal(["src/P/*.cs"], map.Nodes["p.self"].Bind);
    }

    [Theory]
    [InlineData("C:/repo/src/Core/Generated/Gen.Assembly/Gen.Assembly.SyntaxGenerator/Nodes/Foo.g.cs", "Nodes/Foo.g.cs", "Gen.Assembly.SyntaxGenerator")]
    [InlineData("System.Text.Json.SourceGeneration\\System.Text.Json.SourceGeneration.JsonSourceGenerator\\Ctx.g.cs", "Ctx.g.cs", "System.Text.Json.SourceGeneration.JsonSourceGenerator")]
    [InlineData("", "X.g.cs", "unknown generator")]
    public void GeneratorNameComesFromThePathSegmentBeforeTheHintName(string path, string hint, string expected) =>
        Assert.Equal(expected, Archmap.Analysis.RoslynAnalyzer.GeneratorNameOf(path, hint));

    [Fact]
    public void SkeletonPassesTheChecker()
    {
        var analysis = TestData.Analysis();
        var map = Skeleton.Build(analysis, "Example.sln");
        var facts = Checker.Run(map, analysis);
        Assert.Empty(facts.Violations);
        Assert.All(facts.Files, f => Assert.NotNull(f.Leaf));
    }

    [Fact]
    public void SkeletonBindsFrameworkAssembliesTogetherAndOthersByName()
    {
        var analysis = TestData.Analysis(extraExternals:
        [
            new ExternalReference(TestData.B1, "System.Collections", "System.Collections.Generic.List<T>", 1),
            new ExternalReference(TestData.B1, "xunit.v3.assert", "Xunit.Assert", 2),
        ]);
        var map = Skeleton.Build(analysis, "Example.sln");
        Assert.Equal(["ext-sdk", "framework", "xunit-v3-assert"], map.Externals.Keys);
        Assert.Equal(["Ext.Sdk"], map.Externals["ext-sdk"].Bind);
        Assert.Contains("System.*", map.Externals["framework"].Bind);
    }
}
