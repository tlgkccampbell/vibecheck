using System.Diagnostics;

namespace Vibecheck.Analysis;

/// <summary>Specifies how a solution is prepared before it is analyzed.</summary>
public enum SolutionPreparation
{
    /// <summary>The solution is analyzed as it is on disk, which fails for a solution that has never been restored.</summary>
    None,

    /// <summary>
    /// The solution is restored. The analysis runs source generators itself, and its design-time build runs the code
    /// generation targets that hook into compilation, so a restore is enough for most solutions.
    /// </summary>
    Restore,

    /// <summary>
    /// The solution is built. A build is needed only when code generation turns itself off in design-time builds, or
    /// when a project references output that the build produces outside the solution.
    /// </summary>
    Build,
}

/// <summary>
/// Prepares a solution with <c>dotnet restore</c> or <c>dotnet build</c> before it is analyzed. Without a restore,
/// package references and the generators they bring are missing from the analysis.
/// </summary>
public static class SolutionBuild
{
    /// <summary>Prepares a solution quietly, throwing with the output of <c>dotnet</c> when it fails.</summary>
    /// <param name="solution">The path of the solution.</param>
    /// <param name="preparation">How to prepare the solution. <see cref="SolutionPreparation.None"/> returns at once.</param>
    /// <param name="ct">A token that cancels the preparation and kills the process tree.</param>
    public static async Task RunAsync(string solution, SolutionPreparation preparation, CancellationToken ct = default)
    {
        if (preparation == SolutionPreparation.None) return;
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
        var verb = preparation == SolutionPreparation.Build ? "build" : "restore";
        foreach (var a in new[] { verb, solution, "-v", "q", "--nologo", "-nodeReuse:false" }) psi.ArgumentList.Add(a);
        if (preparation == SolutionPreparation.Build) psi.ArgumentList.Add("-p:UseSharedCompilation=false");
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
        if (process.ExitCode != 0) throw new InvalidOperationException($"{(verb == "build" ? "building" : "restoring")} {solution} failed:\n" + await output + await error);
    }
}
