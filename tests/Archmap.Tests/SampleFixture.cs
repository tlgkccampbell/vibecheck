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
        await BuildSampleAsync(Path.Combine(Root, Map.Solution));
        Analysis = await RoslynAnalyzer.AnalyzeAsync(Root, Path.Combine(Root, Map.Solution));
    }

    /// <summary>
    /// Builds the sample so that its state matches a repository people work in: packages
    /// restored, and generator output written to disk under <c>Generated</c>, which is
    /// what the generated-code tests depend on. An up-to-date build takes a few seconds.
    /// </summary>
    private static async Task BuildSampleAsync(string solution)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        // A build started from inside a test host must not try to join or spawn
        // long-lived MSBuild nodes, which is what makes a nested build hang.
        psi.Environment["MSBUILDNODEREUSE"] = "0";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var a in new[] { "build", solution, "-v", "q", "--nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false" }) psi.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("dotnet could not be started");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0) throw new InvalidOperationException("building the sample failed:\n" + await output + await error);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("sample")]
public sealed class SampleCollection : ICollectionFixture<SampleFixture>;
