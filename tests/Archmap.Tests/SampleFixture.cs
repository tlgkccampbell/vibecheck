using Archmap.Analysis;
using Archmap.Format;
using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

/// <summary>
/// Analyzes the Orchard sample solution once for every test in the sample collection.
/// Opening a solution with Roslyn is the slow step, so the tests share the result.
/// </summary>
public sealed class SampleFixture : IAsyncLifetime
{
    public string Root { get; private set; } = "";
    public ArchMap Map { get; private set; } = null!;
    public string MapText { get; private set; } = "";
    public AnalysisResult Analysis { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Archmap.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
        Root = Path.Combine(dir.FullName, "samples", "Orchard");
        MapText = await File.ReadAllTextAsync(Path.Combine(Root, "archmap.json"));
        Map = MapReader.Read(MapText);
        Analysis = await RoslynAnalyzer.AnalyzeAsync(Root, Path.Combine(Root, Map.Solution));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("sample")]
public sealed class SampleCollection : ICollectionFixture<SampleFixture>;
