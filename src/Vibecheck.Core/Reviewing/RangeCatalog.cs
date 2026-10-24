using Vibecheck.Diffing;

namespace Vibecheck.Reviewing;

/// <summary>
/// A range the picker offers: the group it is listed under, a label, the range in the form
/// <see cref="RevisionChangeset.ResolveRange"/> reads, and a detail line.
/// </summary>
/// <param name="Group">One of <c>worktree</c> (the uncommitted work), <c>branch</c> (the current branch), <c>commit</c> and <c>branches</c>.</param>
public sealed record RangeChoice(string Group, string Label, string Range, string? Detail);

/// <summary>Lists the ranges of a repository that a reviewer is likely to want to review. See <c>docs/review-workflow.md</c> section 2.</summary>
public static class RangeCatalog
{
    private const char Separator = '\u001f';

    /// <summary>
    /// Returns the uncommitted work against <c>HEAD</c> when there is any, the current branch against the default
    /// branch, the most recent commits on the current branch, and other local branches that have commits the default
    /// branch lacks, most recently changed first.
    /// </summary>
    public static List<RangeChoice> List(string dir, int commits = 15, int branches = 20)
    {
        var result = new List<RangeChoice>();
        var changed = WorkingTree.ChangedPaths(dir);
        if (changed > 0) result.Add(new RangeChoice("worktree", WorkingTree.Subject, WorkingTree.Name, $"{changed} {(changed == 1 ? "path" : "paths")} changed against HEAD"));
        var main = DefaultBranch(dir);
        var current = Git.TryRun(dir, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (main is not null && current is not null && current != main && !main.EndsWith("/" + current, StringComparison.Ordinal))
        {
            var ahead = Ahead(dir, main, "HEAD");
            if (ahead > 0) result.Add(new RangeChoice("branch", $"{current} against {main}", $"{main}...HEAD", Commits(ahead)));
        }

        var log = Git.TryRun(dir, "log", $"-{commits}", "--format=%H%x1f%P%x1f%s%x1f%cr", "HEAD") ?? "";
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(Separator);
            if (f.Length < 4 || f[1].Length == 0) continue;
            result.Add(new RangeChoice("commit", f[2], $"{f[0]}^..{f[0]}", $"{f[0][..7]} · {f[3]}"));
        }

        if (main is not null)
        {
            var refs = Git.TryRun(dir, "for-each-ref", "refs/heads", "--sort=-committerdate", $"--count={branches}", "--format=%(refname:short)%1f%(committerdate:relative)") ?? "";
            foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split(Separator);
                if (f.Length < 2 || f[0] == current || f[0] == main) continue;
                var ahead = Ahead(dir, main, f[0]);
                if (ahead > 0) result.Add(new RangeChoice("branches", f[0], $"{main}...{f[0]}", $"{Commits(ahead)} · {f[1]}"));
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the branch that ranges are compared against: the remote's default branch when it is known, and
    /// otherwise the first of <c>main</c>, <c>master</c>, <c>origin/main</c> and <c>origin/master</c> that exists.
    /// </summary>
    public static string? DefaultBranch(string dir)
    {
        var remote = Git.TryRun(dir, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        if (!string.IsNullOrEmpty(remote)) return remote;
        return new[] { "main", "master", "origin/main", "origin/master" }
            .FirstOrDefault(b => Git.TryRun(dir, "rev-parse", "--verify", "--quiet", b + "^{commit}") is not null);
    }

    private static int Ahead(string dir, string from, string to) =>
        int.TryParse(Git.TryRun(dir, "rev-list", "--count", $"{from}..{to}"), out var n) ? n : 0;

    private static string Commits(int n) => n == 1 ? "1 commit" : $"{n} commits";
}
