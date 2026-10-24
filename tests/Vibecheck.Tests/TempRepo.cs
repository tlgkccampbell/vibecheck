using System.Diagnostics;

namespace Vibecheck.Tests;

/// <summary>
/// Represents a throwaway git repository in a temporary directory, for tests that need real revisions. Each commit
/// returns its full hash, and disposing the repository deletes the directory.
/// </summary>
internal sealed class TempRepo : IDisposable
{
    public TempRepo()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibecheck-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
        Git("init", "--quiet", "--initial-branch=main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "core.autocrlf", "false");
    }

    /// <summary>Gets the full path of the repository's working tree.</summary>
    public string Path { get; }

    /// <summary>Writes a file at a path relative to the working tree, creating its directory.</summary>
    public void Write(string relative, string text)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>Stages every change, commits it, and returns the new commit's hash.</summary>
    public string Commit(string message)
    {
        Git("add", "--all");
        Git("commit", "--quiet", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    /// <summary>Runs git in the working tree and returns its trimmed output, throwing when it fails.</summary>
    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(Path);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }

    public void Dispose()
    {
        // Git writes its object files read-only, which Directory.Delete refuses on Windows.
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}
