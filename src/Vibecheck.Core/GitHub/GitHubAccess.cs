using System.Diagnostics;
using System.Text.Json;

namespace Vibecheck.GitHub;

/// <summary>
/// Connects to the GitHub repository of a working tree through the <c>gh</c> command. <c>gh</c> names the repository,
/// following the default that <c>gh repo set-default</c> chooses in a fork, and gives the token, honoring
/// <c>GH_TOKEN</c> and <c>GITHUB_TOKEN</c>. The host comes from the repository's address, so GitHub Enterprise Server
/// needs no settings. See <c>docs/pull-requests.md</c> section 3.2.
/// </summary>
public static class GitHubAccess
{
    /// <summary>
    /// Returns a client for the repository of a directory, or <see langword="null"/> with the reason when <c>gh</c> is
    /// missing, is not signed in, or names no GitHub repository.
    /// </summary>
    public static async Task<(IGitHub? GitHub, string? Reason)> ConnectAsync(string dir, TimeSpan timeout)
    {
        var view = await RunAsync(dir, timeout, "repo", "view", "--json", "nameWithOwner,url");
        if (view.Missing) return (null, "The gh command was not found, so pull requests are not listed. Install GitHub CLI and run gh auth login.");
        if (view.Code != 0) return (null, "gh names no GitHub repository here: " + FirstLine(view.Error));
        GitHubRepository repository;
        try
        {
            using var doc = JsonDocument.Parse(view.Output);
            var url = new Uri(doc.RootElement.GetProperty("url").GetString()!);
            var parts = doc.RootElement.GetProperty("nameWithOwner").GetString()!.Split('/');
            repository = new GitHubRepository(url.Host, parts[0], parts[1]);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException or IndexOutOfRangeException)
        {
            return (null, "gh repo view gave an answer that could not be read.");
        }
        var token = await RunAsync(dir, timeout, "auth", "token", "--hostname", repository.Host);
        if (token.Code != 0 || token.Output.Trim().Length == 0)
            return (null, $"gh is not signed in to {repository.Host}, so pull requests are not listed. Run gh auth login.");
        return (new GitHubClient(repository, token.Output.Trim()), null);
    }

    private static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();

    /// <summary>Runs <c>gh</c> in a directory and returns its exit code and output, with <c>Missing</c> set when it cannot be started. A run that outlasts the timeout is killed and fails.</summary>
    private static async Task<(int Code, string Output, string Error, bool Missing)> RunAsync(string dir, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // gh would otherwise ask questions or page its output, which a server has no one to answer.
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GH_PAGER"] = "";
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return (-1, "", "", true);
            using var cancel = new CancellationTokenSource(timeout);
            var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var error = process.StandardError.ReadToEndAsync(cancel.Token);
            try
            {
                await process.WaitForExitAsync(cancel.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (-1, "", "gh took too long to answer", false);
            }
            return (process.ExitCode, await output, await error, false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "", "", true);
        }
    }
}
