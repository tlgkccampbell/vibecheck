using Archmap.Analysis;
using Archmap.Model;

namespace Archmap.Tests;

/// <summary>
/// Builds a small synthetic map and analysis that pass every rule, so that each test
/// can mutate one thing and assert the one violation it expects.
/// </summary>
internal static class TestData
{
    public const string X1 = "src/A/X/X1.cs";
    public const string X2 = "src/A/X/X2.cs";
    public const string Y1 = "src/A/Y/Y1.cs";
    public const string B1 = "src/B/B1.cs";
    public const string S1 = "src/Shared/S1.cs";

    /// <summary>A map with a declared root, a declared container <c>a</c> with two leaves, leaf <c>b</c>, leaf <c>shared</c> and external <c>ext</c>.</summary>
    public static ArchMap Map()
    {
        var map = new ArchMap { Version = 0, Solution = "Example.sln" };
        map.Nodes["a"] = new NodeSpec { Name = "A", Edges = ArchMap.Declared };
        map.Nodes["a.x"] = new NodeSpec { Name = "X", Bind = ["src/A/X/**/*.cs"] };
        map.Nodes["a.y"] = new NodeSpec { Name = "Y", Bind = ["src/A/Y/**/*.cs"] };
        map.Nodes["b"] = new NodeSpec { Name = "B", Bind = ["src/B/**/*.cs"] };
        map.Nodes["shared"] = new NodeSpec { Name = "Shared", Bind = ["src/Shared/**/*.cs"] };
        map.Externals["ext"] = new ExternalSpec { Name = "Ext", Bind = ["Ext.*"] };
        map.Edges.Add(new EdgeSpec { From = "a", To = "shared" });
        map.Edges.Add(new EdgeSpec { From = "b", To = "shared" });
        map.Edges.Add(new EdgeSpec { From = "a.x", To = "a.y" });
        map.Edges.Add(new EdgeSpec { From = "a", To = "ext" });
        return new ArchMap
        {
            Version = 0,
            Solution = map.Solution,
            Nodes = map.Nodes,
            Externals = map.Externals,
            Edges = map.Edges,
            Policies = new PolicySpec { Root = new RootPolicy { Edges = ArchMap.Declared, Externals = ArchMap.Declared } },
        };
    }

    /// <summary>An analysis whose dependencies realize every edge of <see cref="Map"/> and nothing else.</summary>
    public static AnalysisResult Analysis(
        IEnumerable<RawDependency>? extraDependencies = null,
        IEnumerable<SourceFile>? extraFiles = null,
        IEnumerable<ExternalReference>? extraExternals = null,
        IEnumerable<PartialType>? partials = null,
        IEnumerable<GeneratedFile>? generated = null)
    {
        var files = new List<SourceFile>
        {
            new(X1, "h1", "A"), new(X2, "h2", "A"), new(Y1, "h3", "A"), new(B1, "h4", "B"), new(S1, "h5", "Shared"),
        };
        if (extraFiles is not null) files.AddRange(extraFiles);
        var deps = new List<RawDependency>
        {
            new(X1, Y1, "calls", "A.Y.Thing.Run", "Thing", 10),
            new(X1, X2, "references", "A.X.Helper", "Helper", 11),
            new(X1, S1, "references", "Shared.Result", "Result", 12),
            new(B1, S1, "constructs", "Shared.Error", "Error", 5),
        };
        if (extraDependencies is not null) deps.AddRange(extraDependencies);
        var externals = new List<ExternalReference> { new(X1, "Ext.Sdk", "Ext.Sdk.Client", 20) };
        if (extraExternals is not null) externals.AddRange(extraExternals);
        return new AnalysisResult
        {
            Files = files,
            GeneratedFiles = generated?.ToList() ?? [],
            Dependencies = deps,
            ExternalReferences = externals,
            Unresolvable = [],
            PartialTypes = partials?.ToList() ?? [],
            Projects =
            [
                new ProjectInfo("Example.A", "src/A", [X1, X2, Y1]),
                new ProjectInfo("Example.B", "src/B", [B1]),
                new ProjectInfo("Example.Shared", "src/Shared", [S1]),
            ],
        };
    }
}
