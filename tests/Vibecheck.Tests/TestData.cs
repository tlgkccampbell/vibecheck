using Vibecheck.Analysis;
using Vibecheck.Model;

namespace Vibecheck.Tests;

/// <summary>
/// Builds a small synthetic map and analysis whose check produces no warnings, so
/// that each test can mutate one thing and assert the one effect it expects.
/// </summary>
internal static class TestData
{
    public const string X1 = "src/A/X/X1.cs";
    public const string X2 = "src/A/X/X2.cs";
    public const string Y1 = "src/A/Y/Y1.cs";
    public const string B1 = "src/B/B1.cs";
    public const string S1 = "src/Shared/S1.cs";

    public const string Thing = "A.X.Thing";
    public const string Helper = "A.X.Helper";
    public const string Other = "A.Y.Other";
    public const string Service = "B.Service";
    public const string Result = "Shared.Result";
    public const string Error = "Shared.Error";

    /// <summary>A map with a container <c>a</c> holding two leaves, leaf <c>b</c>, leaf <c>shared</c> and external <c>ext</c>.</summary>
    public static ArchitectureMap Map()
    {
        var map = new ArchitectureMap { Version = 0, Solution = "Example.sln" };
        map.Nodes["a"] = new NodeSpec { Name = "A" };
        map.Nodes["a.x"] = new NodeSpec { Name = "X", Bind = ["A.X.**"] };
        map.Nodes["a.y"] = new NodeSpec { Name = "Y", Bind = ["A.Y.**"] };
        map.Nodes["b"] = new NodeSpec { Name = "B", Bind = ["B.**"] };
        map.Nodes["shared"] = new NodeSpec { Name = "Shared", Bind = ["Shared.**"] };
        map.Externals["ext"] = new ExternalSpec { Name = "Ext", Bind = ["Ext.*"] };
        return map;
    }

    /// <summary>
    /// An analysis with four dependencies: Thing calls Other (a.x to a.y), Thing uses
    /// Helper (inside a.x), Thing uses Result (a.x to shared) and Service constructs
    /// Error (b to shared), plus one external reference from Thing to Ext.Sdk.
    /// </summary>
    public static AnalysisResult Analysis(
        IEnumerable<RawDependency>? extraDependencies = null,
        IEnumerable<TypeInfo>? extraTypes = null,
        IEnumerable<SourceFile>? extraFiles = null,
        IEnumerable<ExternalReference>? extraExternals = null,
        Func<TypeInfo, TypeInfo>? adjust = null)
    {
        var files = new List<SourceFile>
        {
            new(X1, "h1", "A"), new(X2, "h2", "A"), new(Y1, "h3", "A"), new(B1, "h4", "B"), new(S1, "h5", "Shared"),
        };
        if (extraFiles is not null) files.AddRange(extraFiles);
        var types = new List<TypeInfo>
        {
            new(Thing, "Example.A", [X1], Summary: "Does the thing."),
            new(Helper, "Example.A", [X2]),
            new(Other, "Example.A", [Y1]),
            new(Service, "Example.B", [B1]),
            new(Result, "Example.Shared", [S1]),
            new(Error, "Example.Shared", [S1]),
        };
        if (extraTypes is not null) types.AddRange(extraTypes);
        if (adjust is not null) types = types.Select(adjust).ToList();
        var deps = new List<RawDependency>
        {
            new(Thing, Other, "calls", "A.Y.Other.Run", X1, 10),
            new(Thing, Helper, "references", "A.X.Helper", X1, 11),
            new(Thing, Result, "references", "Shared.Result", X1, 12),
            new(Service, Error, "constructs", "Shared.Error", B1, 5),
        };
        if (extraDependencies is not null) deps.AddRange(extraDependencies);
        var externals = new List<ExternalReference> { new(Thing, "Ext.Sdk", "Ext.Sdk.Client", "references", "Ext.Sdk.Client", X1, 20) };
        if (extraExternals is not null) externals.AddRange(extraExternals);
        return new AnalysisResult
        {
            Files = files,
            Types = types,
            Dependencies = deps,
            ExternalReferences = externals,
            Unresolvable = [],
            Projects =
            [
                new ProjectInfo("Example.A", "Example.A", "src/A"),
                new ProjectInfo("Example.B", "Example.B", "src/B"),
                new ProjectInfo("Example.Shared", "Example.Shared", "src/Shared"),
            ],
        };
    }
}
