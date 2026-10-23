using Archmap.Analysis;
using Archmap.Bootstrap;
using Archmap.Checking;
using Archmap.Format;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

public class ExclusionTests
{
    private const string Regex = "System.Text.RegularExpressions.Generated.Utilities";
    private const string RegexFile = "src/A/Generated/Regex.g.cs";

    /// <summary>The baseline analysis plus a generated regex helper that <c>A.X.Thing</c> uses, declared in a file of its own.</summary>
    private static AnalysisResult WithRegex() => TestData.Analysis(
        extraTypes: [new TypeInfo(Regex, "Example.A", [RegexFile], Generator: "RegexGenerator")],
        extraFiles: [new SourceFile(RegexFile, "h9", "A", "RegexGenerator")],
        extraDependencies: [new RawDependency(TestData.Thing, Regex, "calls", Regex + ".IsWordChar", TestData.X1, 30)],
        extraExternals: [new ExternalReference(Regex, "System.Runtime.Intrinsics", "System.Runtime.Intrinsics.Vector128", RegexFile, 5)]);

    private static ArchMap Excluding(params string[] patterns)
    {
        var map = TestData.Map();
        map.Exclude.AddRange(patterns);
        return map;
    }

    [Fact]
    public void WithoutAnExclusionTheGeneratedTypeIsUnmapped()
    {
        var facts = Checker.Run(TestData.Map(), WithRegex());
        var v = Assert.Single(facts.Violations);
        Assert.Equal("partition", v.Rule);
        Assert.Contains(Regex, v.Message);
    }

    [Fact]
    public void AnExcludedTypeIsNotPartitionedAndCreatesNoEdges()
    {
        var facts = Checker.Run(Excluding("System.**"), WithRegex());
        Assert.Empty(facts.Violations);
        Assert.Equal(new ExcludedCounts(1, 1), facts.Excluded);
        Assert.DoesNotContain(facts.Types, t => t.Name == Regex);
        Assert.DoesNotContain(facts.Files, f => f.Path == RegexFile);
        Assert.DoesNotContain(facts.Dependencies, d => d.To == Regex);
        Assert.DoesNotContain(facts.ExternalRefs, e => e.From == Regex);
        Assert.Equal(4, facts.Dependencies.Count);
    }

    [Fact]
    public void AFileThatAlsoDeclaresAnIncludedTypeIsKept()
    {
        var analysis = TestData.Analysis(
            extraTypes: [new TypeInfo(Regex, "Example.A", [TestData.X2], Generator: "RegexGenerator")]);
        var facts = Checker.Run(Excluding("System.**"), analysis);
        Assert.Empty(facts.Violations);
        Assert.Equal(new ExcludedCounts(1, 0), facts.Excluded);
        Assert.Equal(["a.x"], facts.Files.Single(f => f.Path == TestData.X2).Leaves);
    }

    [Fact]
    public void AnAssemblyQualifiedPatternExcludesAWholeProject()
    {
        var facts = Checker.Run(Excluding("Example.B::**"), TestData.Analysis());
        Assert.Equal(new ExcludedCounts(1, 1), facts.Excluded);
        Assert.DoesNotContain(facts.Types, t => t.Name == TestData.Service);
        // The declared edge b → shared is now dead, which is the map's problem to fix, not the exclusion's.
        var v = Assert.Single(facts.Violations);
        Assert.Equal("realization", v.Rule);
        Assert.Contains("b → shared", v.Message);
    }

    [Fact]
    public void ExclusionWinsOverTheAttributeWithAWarning()
    {
        var analysis = TestData.Analysis(adjust: t => t.Name == TestData.Service ? t with { Attribute = "a.x" } : t);
        var facts = Checker.Run(Excluding("B.**"), analysis);
        Assert.DoesNotContain(facts.Types, t => t.Name == TestData.Service);
        Assert.Contains(facts.Warnings, w => w.Contains(TestData.Service) && w.Contains("excludes it"));
    }

    [Fact]
    public void AMalformedExcludePatternFailsWellFormedness()
    {
        var facts = Checker.Run(Excluding("System.***"), TestData.Analysis());
        var v = Assert.Single(facts.Violations);
        Assert.Equal("well-formedness", v.Rule);
        Assert.StartsWith("exclude:", v.Message);
    }

    [Fact]
    public void TheExcludeListRoundTripsInCanonicalOrder()
    {
        var text = MapWriter.Write(Excluding("System.**", "Example.Tests::**"));
        Assert.Contains("  \"solution\": \"Example.sln\",\n  \"exclude\": [\"System.**\", \"Example.Tests::**\"],\n  \"nodes\": {", text);
        Assert.Equal(["System.**", "Example.Tests::**"], MapReader.Read(text).Exclude);
        Assert.Contains("  \"exclude\": [],\n", MapWriter.Write(TestData.Map()));
        var ex = Assert.Throws<MapFormatException>(() => MapReader.Read("{ \"version\": 0, \"solution\": \"x\", \"exclude\": \"System.**\" }"));
        Assert.Contains(ex.Errors, e => e.Contains("exclude must be an array"));
    }

    [Fact]
    public void TheSkeletonExcludesSystemNamespacesAndTestProjects()
    {
        var analysis = TestData.Analysis(
            extraTypes: [new TypeInfo("Example.Tests.ThingTests", "Example.Tests", ["tests/ThingTests.cs"])],
            extraFiles: [new SourceFile("tests/ThingTests.cs", "h8", "Example.Tests")],
            extraExternals: [new ExternalReference("Example.Tests.ThingTests", "xunit.v3.assert", "Xunit.Assert", "tests/ThingTests.cs", 1)]);
        analysis = analysis with { Projects = [.. analysis.Projects, new ProjectInfo("Example.Tests", "Example.Tests", "tests")] };
        var map = Skeleton.Build(analysis, "Example.sln");
        Assert.Equal(["System.**", "Example.Tests::**"], map.Exclude);
        Assert.DoesNotContain("tests", map.Nodes.Keys);
        Assert.DoesNotContain("xunit-v3-assert", map.Externals.Keys);
        Assert.Empty(Checker.Run(map, analysis).Violations);
    }

    [Fact]
    public void TheSkeletonDoesNotMistakeAProjectForATestProjectByName()
    {
        var map = Skeleton.Build(TestData.Analysis(), "Example.sln");
        Assert.Equal(["System.**"], map.Exclude);
    }
}
