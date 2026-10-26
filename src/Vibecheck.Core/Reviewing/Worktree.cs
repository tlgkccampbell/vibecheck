using System.Diagnostics;
using System.Text.RegularExpressions;
using Vibecheck.Diffing;

namespace Vibecheck.Reviewing;

/// <summary>
/// Represents a commit checked out into a temporary, detached git worktree, so that the reviewer's own working tree is
/// never touched. Disposing it removes the worktree and its directory.
/// </summary>
/// <remarks>
/// A worktree's directory is named after the process that made it. A directory can outlive its process: a process
/// that is killed never disposes its worktrees, and a generator the analysis loads keeps its assembly locked until
/// the process exits. The first worktree that a process adds therefore removes the directories of processes that
/// are no longer running.
/// </remarks>
public sealed partial class Worktree : IDisposable
{
    private static readonly string Prefix = "vibecheck-" + Environment.ProcessId + "-";
    private static int swept;
    private readonly string top;
    private bool disposed;

    private Worktree(string top, string path, string commit)
    {
        this.top = top;
        Path = path;
        Commit = commit;
    }

    /// <summary>Gets the directory the commit is checked out in.</summary>
    public string Path { get; }

    /// <summary>Gets the commit that is checked out.</summary>
    public string Commit { get; }

    /// <summary>Checks out a commit of the repository whose top directory is given into a new temporary worktree.</summary>
    public static Worktree Add(string top, string commit)
    {
        if (Interlocked.Exchange(ref swept, 1) == 0) RemoveAbandoned(top, System.IO.Path.GetTempPath());
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Git.Run(top, "worktree", "add", "--detach", "--quiet", path, commit);
        }
        catch
        {
            DeleteQuietly(path);
            throw;
        }
        return new Worktree(top, path, commit);
    }

    /// <summary>Removes the worktree from the repository and deletes its directory.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Git.TryRun(top, "worktree", "remove", "--force", Path);
        DeleteQuietly(Path);
    }

    /// <summary>
    /// Removes the worktree directories in a temporary directory whose processes are no longer running, and then
    /// prunes the worktrees of the repository whose top directory is given. A worktree of another repository is
    /// pruned by the next process that adds a worktree of that repository.
    /// </summary>
    internal static void RemoveAbandoned(string top, string temp)
    {
        foreach (var dir in Directory.EnumerateDirectories(temp, "vibecheck-*"))
        {
            var match = OwnedName().Match(System.IO.Path.GetFileName(dir));
            if (!match.Success || IsRunning(int.Parse(match.Groups[1].Value))) continue;
            DeleteQuietly(dir);
        }
        Git.TryRun(top, "worktree", "prune");
    }

    /// <summary>Checks whether a process with the identifier is running. A reused identifier counts as running, which keeps its directories.</summary>
    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // A process this user may not inspect, such as an elevated one, is running.
            return true;
        }
    }

    [GeneratedRegex(@"^vibecheck-(\d+)-[0-9a-f]+$")]
    private static partial Regex OwnedName();

    /// <summary>Deletes a directory that git may have left behind, clearing the read-only flag git puts on its object files.</summary>
    private static void DeleteQuietly(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open, such as by a build server, leaves the directory for the system to clean.
        }
    }
}
