using Vibecheck.Analysis;
using Vibecheck.Format;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>
/// Analyzes the Orchard sample solution once for every test in the sample collection.
/// Opening a solution with Roslyn is the slow step, so the tests share the result.
/// </summary>
public sealed class SampleFixture : IAsyncLifetime
{
    public string Root { get; private set; } = "";
    public ArchitectureMap Map { get; private set; } = null!;
    public string MapText { get; private set; } = "";
    public AnalysisResult Analysis { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vibecheck.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
        Root = Path.Combine(dir.FullName, "samples", "Orchard");
        MapText = await File.ReadAllTextAsync(Path.Combine(Root, "vibecheck.json"));
        Map = MapReader.Read(MapText);
        // The build restores packages and writes generator output to disk under Generated, which is what the
        // generated-code tests depend on. An up-to-date build takes a few seconds.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await SolutionBuild.RunAsync(Path.Combine(Root, Map.Solution), SolutionPreparation.Build, timeout.Token);
        Analysis = await RoslynAnalyzer.AnalyzeAsync(Root, Path.Combine(Root, Map.Solution));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("sample")]
public sealed class SampleCollection : ICollectionFixture<SampleFixture>;
