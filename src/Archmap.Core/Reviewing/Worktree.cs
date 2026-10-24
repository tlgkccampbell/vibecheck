using Archmap.Diffing;

namespace Archmap.Reviewing;

/// <summary>
/// Represents a commit checked out into a temporary, detached git worktree, so that the reviewer's own working tree is
/// never touched. Disposing it removes the worktree and its directory.
/// </summary>
public sealed class Worktree : IDisposable
{
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
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "archmap-" + Guid.NewGuid().ToString("N")[..12]);
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
