namespace Archmap.Diffing;

/// <summary>
/// Turns the uncommitted work in a repository into a commit object that a review can compare like any other, without
/// touching the reviewer's index, working tree or refs. The commit is called a snapshot. Its author, committer and
/// dates are fixed, so the same tree on the same parent always gives the same hash, and every cache keyed by commit
/// works for it unchanged. See <c>docs/review-workflow.md</c> section 4.6.
/// </summary>
public static class WorkingTree
{
    /// <summary>The name a range uses for the uncommitted work. Git does not allow a colon in a ref name, so no branch or tag can have it.</summary>
    public const string Name = ":worktree";

    /// <summary>The subject of every snapshot.</summary>
    public const string Subject = "Uncommitted changes";

    private const string Email = "snapshot@archmap.invalid";

    /// <summary>
    /// Returns a snapshot of the working tree of the repository that contains a directory, with <c>HEAD</c> as its
    /// parent. Tracked and untracked files are included, and files that <c>.gitignore</c> excludes are left out. When
    /// nothing is uncommitted, returns the <c>HEAD</c> commit itself.
    /// </summary>
    public static string Snapshot(string dir)
    {
        var top = Path.GetFullPath(Git.Run(dir, "rev-parse", "--show-toplevel"));
        var head = Git.TryRun(top, "rev-parse", "--verify", "--quiet", "HEAD^{commit}") ?? throw new ArgumentException("the repository has no commit to compare the uncommitted work with");
        var scratch = Path.Combine(Path.GetTempPath(), "archmap-index-" + Guid.NewGuid().ToString("N")[..12]);
        var index = Path.Combine(scratch, "index");
        Directory.CreateDirectory(scratch);
        try
        {
            // A copy of the real index carries git's record of each file's size and time, so that git add rehashes only
            // the files that changed. A copy that git cannot use, such as a split index, gives way to an empty index.
            var real = Path.GetFullPath(Path.Combine(top, Git.Run(top, "rev-parse", "--git-path", "index")));
            if (File.Exists(real)) File.Copy(real, index);
            var environment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = index,
                ["GIT_AUTHOR_NAME"] = "archmap", ["GIT_AUTHOR_EMAIL"] = Email, ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z",
                ["GIT_COMMITTER_NAME"] = "archmap", ["GIT_COMMITTER_EMAIL"] = Email, ["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z",
            };
            string tree;
            try
            {
                tree = WriteTree(top, environment);
            }
            catch (InvalidOperationException) when (File.Exists(index))
            {
                File.Delete(index);
                tree = WriteTree(top, environment);
            }
            if (tree == Git.Run(top, "rev-parse", "HEAD^{tree}")) return head;
            return Git.Run(top, environment, "commit-tree", tree, "-p", head, "-m", Subject);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* The system cleans the temporary folder. */ }
        }
    }

    /// <summary>Gets a value indicating whether a commit is a snapshot of uncommitted work, by its fixed author.</summary>
    public static bool IsSnapshot(string dir, string commit) =>
        Git.TryRun(dir, "log", "-1", "--format=%ae", commit) == Email;

    /// <summary>Returns how many paths <c>git status</c> reports as changed or untracked, which is zero for a clean working tree.</summary>
    public static int ChangedPaths(string dir) =>
        (Git.TryRun(dir, "status", "--porcelain", "--untracked-files=all") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string WriteTree(string top, Dictionary<string, string> environment)
    {
        Git.Run(top, environment, "add", "--all", "--", ".");
        return Git.Run(top, environment, "write-tree");
    }
}
