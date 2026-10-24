using System.Diagnostics;

namespace Vibecheck.Analysis;

/// <summary>
/// Builds a solution with <c>dotnet build</c>, so that packages are restored and source generators have run before
/// the solution is analyzed. Without a build, the types that generators produce are missing from the analysis.
/// </summary>
public static class SolutionBuild
{
    /// <summary>Builds a solution quietly, throwing with the build's output when it fails.</summary>
    public static async Task RunAsync(string solution, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        // A build started from inside another .NET process, such as a test host, must not try to join or spawn
        // long-lived MSBuild nodes, which is what makes a nested build hang.
        psi.Environment["MSBUILDNODEREUSE"] = "0";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var a in new[] { "build", solution, "-v", "q", "--nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false" }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet could not be started");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"building {solution} failed:\n" + await output + await error);
    }
}
