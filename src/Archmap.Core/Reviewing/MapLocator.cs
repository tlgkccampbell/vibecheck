using Archmap.Diffing;
using Archmap.Format;

namespace Archmap.Reviewing;

/// <summary>A map file read from a revision: the directory that holds it, relative to the repository's top, its text, and the text of the layout file beside it, if any.</summary>
public sealed record LocatedMap(string Directory, string Text, string? LayoutText);

/// <summary>
/// Finds the map file or the solution that a review uses. Both are read from a commit, not from the working tree, so
/// that a review of a range sees the files as they are at that range.
/// </summary>
public static class MapLocator
{
    /// <summary>The name of the map file in a repository.</summary>
    public const string FileName = "archmap.json";

    /// <summary>
    /// Returns the map file at a commit that is nearest a start directory: in the start directory itself or in the
    /// closest of its ancestors up to the repository's top. Returns <see langword="null"/> when there is none.
    /// </summary>
    /// <param name="top">The repository's top directory.</param>
    /// <param name="start">The start directory, relative to <paramref name="top"/>.</param>
    /// <param name="commit">The commit to read from.</param>
    public static LocatedMap? FindMap(string top, string start, string commit)
    {
        foreach (var dir in SelfAndAncestors(start))
        {
            var text = ReadFile(top, commit, Join(dir, FileName));
            if (text is null) continue;
            return new LocatedMap(dir, text, ReadFile(top, commit, Join(dir, LayoutFile.FileName)));
        }
        return null;
    }

    /// <summary>
    /// Returns the path, relative to the repository's top, of the solution file at a commit that a review without a map
    /// analyzes. That is the shallowest <c>.sln</c> or <c>.slnx</c> file under the start directory, or in the whole
    /// repository when there is none under it.
    /// </summary>
    /// <exception cref="IOException">There is no solution file, or more than one at the shallowest depth.</exception>
    public static string FindSolution(string top, string start, string commit)
    {
        var all = Git.Run(top, "ls-tree", "-r", "--name-only", "--full-tree", commit)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var under = start == "." ? all : all.Where(p => p.StartsWith(start + "/", StringComparison.Ordinal)).ToList();
        var candidates = under.Count > 0 ? under : all;
        if (candidates.Count == 0) throw new IOException($"there is no solution file at {commit[..Math.Min(8, commit.Length)]}; pass --solution or --map");
        var depth = candidates.Min(Depth);
        var shallowest = candidates.Where(p => Depth(p) == depth).ToList();
        if (shallowest.Count > 1) throw new IOException($"there are several solutions ({string.Join(", ", shallowest)}); pass --solution to choose one");
        return shallowest[0];
    }

    /// <summary>Returns a directory's path relative to the repository's top with forward slashes, or <c>.</c> for the top itself.</summary>
    public static string Relative(string top, string dir)
    {
        var relative = Path.GetRelativePath(top, dir).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal)) throw new ArgumentException($"{dir} is not inside the repository at {top}");
        return relative;
    }

    /// <summary>Returns the directory a relative file path lies in, or <c>.</c> for a file at the top.</summary>
    public static string DirectoryOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "." : path[..i];
    }

    private static int Depth(string path) => path.Count(c => c == '/');

    private static IEnumerable<string> SelfAndAncestors(string start)
    {
        for (var dir = start; ; dir = DirectoryOf(dir))
        {
            yield return dir;
            if (dir == ".") yield break;
        }
    }

    private static string Join(string dir, string name) => dir == "." ? name : dir + "/" + name;

    private static string? ReadFile(string top, string commit, string path) =>
        Git.TryRun(top, "cat-file", "-e", $"{commit}:{path}") is null ? null : Git.RunExact(top, "show", $"{commit}:{path}");
}
