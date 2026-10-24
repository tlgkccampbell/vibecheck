using Vibecheck.Analysis;
using Vibecheck.Bootstrap;
using Vibecheck.Checking;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

public class SkeletonTests
{
    private static AnalysisResult Solution(IEnumerable<ProjectInfo> projects, IEnumerable<TypeInfo> types) => new()
    {
        Files = types.SelectMany(t => t.Files).Distinct().Select(f => new SourceFile(f, "h", "p")).ToList(),
        Types = types.ToList(),
        Dependencies = [],
        ExternalReferences = [],
        Unresolvable = [],
        Projects = projects.ToList(),
    };

    private static TypeInfo T(string name, string assembly) => new(name, assembly, [$"src/{assembly}/{name}.cs"]);

    [Fact]
    public void StripsCommonProjectPrefixAndMirrorsNamespaces()
    {
        var map = Skeleton.Build(Solution(
            [new ProjectInfo("Example.Billing", "Example.Billing", "src/Example.Billing"), new ProjectInfo("Example.Auth", "Example.Auth", "src/Example.Auth")],
            [T("Example.Billing.Core.Payments.Capture", "Example.Billing"), T("Example.Billing.Core.Service", "Example.Billing"), T("Example.Billing.Api.Endpoints", "Example.Billing"), T("Example.Auth.Session", "Example.Auth")]),
            "Example.sln");

        Assert.Equal(["auth", "billing", "billing.api", "billing.core", "billing.core.payments", "billing.core.self"], map.Nodes.Keys);
        Assert.Equal(["Example.Auth.**"], map.Nodes["auth"].Bind);
        Assert.Equal(["Example.Billing.Api.**"], map.Nodes["billing.api"].Bind);
        Assert.Equal(["Example.Billing.Core.*"], map.Nodes["billing.core.self"].Bind);
        Assert.Equal(["Example.Billing.Core.Payments.**"], map.Nodes["billing.core.payments"].Bind);
        Assert.False(map.Nodes["billing.core"].IsLeaf);
        Assert.Equal("Example.Billing", map.Nodes["billing"].Name);
        Assert.Equal("Payments", map.Nodes["billing.core.payments"].Name);
        Assert.Empty(map.Views);
    }

    [Fact]
    public void SingleProjectKeepsItsFullName()
    {
        var map = Skeleton.Build(Solution([new ProjectInfo("Example.Only", "Example.Only", "src")], [T("Example.Only.A", "Example.Only")]), "x.sln");
        Assert.Equal(["example-only"], map.Nodes.Keys);
    }

    [Fact]
    public void DoesNotCollapseSingleChildChains()
    {
        var map = Skeleton.Build(Solution([new ProjectInfo("P", "P", "src/P")], [T("P.Features.Payments.A", "P"), T("P.Root", "P")]), "x.sln");
        Assert.Contains("p.features.payments", map.Nodes.Keys);
        Assert.False(map.Nodes["p.features"].IsLeaf);
        Assert.Equal(["P.*"], map.Nodes["p.self"].Bind);
    }

    [Fact]
    public void CollidingNormalizedNamesGetSuffixes()
    {
        var map = Skeleton.Build(Solution([new ProjectInfo("P", "P", "src/P")], [T("P.Foo.A", "P"), T("P.foo.B", "P")]), "x.sln");
        Assert.Equal(["p", "p.foo", "p.foo-2"], map.Nodes.Keys);
    }

    [Fact]
    public void GlobalNamespaceTypesGetTheirOwnLeafBoundByAssembly()
    {
        var map = Skeleton.Build(Solution([new ProjectInfo("P", "P", "src/P")], [T("P.Core.A", "P"), T("Program", "P")]), "x.sln");
        Assert.Equal(["P::*"], map.Nodes["p.global"].Bind);
        Assert.False(map.Nodes["p"].IsLeaf);
        var only = Skeleton.Build(Solution([new ProjectInfo("Q", "Q", "src/Q")], [T("Program", "Q")]), "x.sln");
        Assert.Equal(["Q::*"], only.Nodes["q"].Bind);
    }

    [Fact]
    public void SharedNamespacesAreQualifiedByAssembly()
    {
        var map = Skeleton.Build(Solution(
            [new ProjectInfo("Example.Core", "P", "src/P"), new ProjectInfo("Example.Tests", "P.Tests", "tests/P.Tests")],
            [T("P.Core.A", "P"), T("P.Core.ATests", "P.Tests")]), "x.sln");
        Assert.Equal(["P::P.Core.**"], map.Nodes["core"].Bind);
        Assert.Equal(["P.Tests::P.Core.**"], map.Nodes["tests"].Bind);
    }

    [Fact]
    public void GeneratedTypesInSystemNamespacesAreExcludedBeforeTheRootIsDerived()
    {
        var regex = new TypeInfo("System.Text.RegularExpressions.Generated.Utilities", "P", ["src/P/Generated/Regex.g.cs"], Generator: "RegexGenerator");
        var map = Skeleton.Build(Solution([new ProjectInfo("P", "P", "src/P")], [T("P.Core.A", "P"), T("P.Core.B", "P"), regex]), "x.sln");
        Assert.Equal(["p"], map.Nodes.Keys);
        Assert.Equal(["P.Core.**"], map.Nodes["p"].Bind);
    }

    [Fact]
    public void TypesOutsideTheRootNamespaceAreEmittedFromTheTopOfTheirOwnHierarchy()
    {
        // Hand-written types in two unrelated namespaces leave the project with no root namespace, so both are emitted in full.
        var map = Skeleton.Build(Solution([new ProjectInfo("P", "P", "src/P")], [T("P.Core.A", "P"), T("P.Core.B", "P"), T("Other.Place.C", "P")]), "x.sln");
        Assert.Equal(["p", "p.other", "p.other.place", "p.p", "p.p.core"], map.Nodes.Keys);
        Assert.Equal(["P.Core.**"], map.Nodes["p.p.core"].Bind);
        Assert.Equal(["Other.Place.**"], map.Nodes["p.other.place"].Bind);
    }

    [Fact]
    public void SelfPatternIsQualifiedOnlyWhenTheExactNamespaceIsShared()
    {
        var map = Skeleton.Build(Solution(
            [new ProjectInfo("Example.Core", "P", "src/P"), new ProjectInfo("Example.Cli", "P.Cli", "src/P.Cli")],
            [T("P.A", "P"), T("P.Core.B", "P"), T("P.Cli.Main", "P.Cli")]), "x.sln");
        Assert.Equal(["P.*"], map.Nodes["core.self"].Bind);
        Assert.Equal(["P.Core.**"], map.Nodes["core.core"].Bind);
        Assert.Equal(["P.Cli.**"], map.Nodes["cli"].Bind);
    }

    [Fact]
    public void RootNamespaceIsTheLongestSharedPrefix()
    {
        Assert.Equal("A.B", Skeleton.RootNamespace(["A.B.C", "A.B", "A.B.D.E"]));
        Assert.Equal("", Skeleton.RootNamespace(["A.B", "C.D"]));
        Assert.Equal("A", Skeleton.RootNamespace(["A"]));
    }

    [Theory]
    [InlineData("C:/repo/src/Core/Generated/Gen.Assembly/Gen.Assembly.SyntaxGenerator/Nodes/Foo.g.cs", "Nodes/Foo.g.cs", "Gen.Assembly.SyntaxGenerator")]
    [InlineData("System.Text.Json.SourceGeneration\\System.Text.Json.SourceGeneration.JsonSourceGenerator\\Ctx.g.cs", "Ctx.g.cs", "System.Text.Json.SourceGeneration.JsonSourceGenerator")]
    [InlineData("", "X.g.cs", "unknown generator")]
    public void GeneratorNameComesFromThePathSegmentBeforeTheHintName(string path, string hint, string expected) =>
        Assert.Equal(expected, RoslynAnalyzer.GeneratorNameOf(path, hint));

    [Fact]
    public void SkeletonLeavesNothingUnsorted()
    {
        var analysis = TestData.Analysis();
        var map = Skeleton.Build(analysis, "Example.sln");
        var facts = Checker.Run(map, analysis);
        Assert.Empty(facts.Violations);
        Assert.Empty(facts.Warnings);
        Assert.DoesNotContain(facts.Types, t => t.Leaf == ArchitectureMap.Unsorted);
    }

    [Fact]
    public void SkeletonBindsFrameworkAssembliesTogetherAndOthersByName()
    {
        var analysis = TestData.Analysis(extraExternals:
        [
            new ExternalReference(TestData.Service, "System.Collections", "System.Collections.Generic.List<T>", TestData.B1, 1),
            new ExternalReference(TestData.Service, "Newtonsoft.Json", "Newtonsoft.Json.JsonConvert", TestData.B1, 2),
        ]);
        var map = Skeleton.Build(analysis, "Example.sln");
        Assert.Equal(["ext-sdk", "framework", "newtonsoft-json"], map.Externals.Keys);
        Assert.Equal(["Ext.Sdk"], map.Externals["ext-sdk"].Bind);
        Assert.Contains("System.*", map.Externals["framework"].Bind);
    }
}
