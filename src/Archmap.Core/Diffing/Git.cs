using System.Diagnostics;

namespace Archmap.Diffing;

/// <summary>Runs git commands in a directory and returns their output.</summary>
public static class Git
{
    /// <summary>Runs git in a directory and returns its trimmed output, throwing when git cannot start or fails.</summary>
    public static string Run(string dir, params string[] args)
    {
        var (code, output, error) = Start(dir, args);
        if (code != 0) throw new InvalidOperationException($"git {args.FirstOrDefault()} failed: {error.Trim()}");
        return output.Trim();
    }

    /// <summary>Runs git in a directory and returns its output exactly as written, throwing when git cannot start or fails.</summary>
    public static string RunExact(string dir, params string[] args)
    {
        var (code, output, error) = Start(dir, args);
        if (code != 0) throw new InvalidOperationException($"git {args.FirstOrDefault()} failed: {error.Trim()}");
        return output;
    }

    /// <summary>Runs git in a directory and returns its trimmed output, or <see langword="null"/> when git cannot start or fails.</summary>
    public static string? TryRun(string dir, params string[] args)
    {
        try
        {
            var (code, output, _) = Start(dir, args);
            return code == 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static (int Code, string Output, string Error) Start(string dir, string[] args)
    {
        // Git writes file contents and paths as UTF-8, whatever the console's code page is.
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        // Standard error is read on another thread, so that a full pipe on either stream cannot stall git.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }
}
