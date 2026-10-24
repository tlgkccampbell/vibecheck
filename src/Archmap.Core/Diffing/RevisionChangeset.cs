using Archmap.Analysis;
using Archmap.Checking;
using Archmap.Model;

namespace Archmap.Diffing;

/// <summary>Analyzes a solution checked out at some path. <see cref="RoslynAnalyzer.AnalyzeAsync"/> is the real one.</summary>
public delegate Task<AnalysisResult> SolutionAnalyzer(string root, string solution, IProgress<string>? progress, CancellationToken ct);

/// <summary>
/// Describes one changeset to produce: the directory inside the repository that the map's paths are relative to, the
/// map that describes both ends, the layout beside it, the two commits, and whether to build each before analysis.
/// </summary>
public sealed record ChangesetRequest(string Root, ArchMap Map, string MapText, string? LayoutText, string BaseCommit, string HeadCommit, bool Build = true);

/// <summary>Holds a changeset together with the facts of its two ends, whose warnings a caller may want to report.</summary>
public sealed record ChangesetResult(Changeset Changeset, Facts Base, Facts Head);

/// <summary>
/// Produces a changeset from two revisions of a repository. Each revision is checked out into a temporary git
/// worktree, built, analyzed and checked against one map, so the working tree is never touched and the map need not be
/// committed at either revision.
/// </summary>
public static class RevisionChangeset
{
    /// <summary>
    /// Resolves the revisions a changeset compares to two commit hashes. One argument may be a range: <c>a..b</c>
    /// compares <c>a</c> with <c>b</c>, and <c>a...b</c> compares the merge base of the two with <c>b</c>, as a pull
    /// request does. An empty side of a range, or a missing head, means <c>HEAD</c>.
    /// </summary>
    public static (string Base, string Head) ResolveRange(string dir, string first, string? second = null)
    {
        if (second is not null) return (Commit(dir, first), Commit(dir, second));
        var three = first.IndexOf("...", StringComparison.Ordinal);
        if (three >= 0)
        {
            var head = Commit(dir, OrHead(first[(three + 3)..]));
            return (Git.Run(dir, "merge-base", Commit(dir, OrHead(first[..three])), head), head);
        }
        var two = first.IndexOf("..", StringComparison.Ordinal);
        if (two >= 0) return (Commit(dir, OrHead(first[..two])), Commit(dir, OrHead(first[(two + 2)..])));
        return (Commit(dir, first), Commit(dir, "HEAD"));
    }

    private static string OrHead(string rev) => rev.Length == 0 ? "HEAD" : rev;

    private static string Commit(string dir, string rev) =>
        Git.TryRun(dir, "rev-parse", "--verify", "--quiet", rev + "^{commit}") ?? throw new ArgumentException($"\"{rev}\" does not name a commit");

    /// <summary>
    /// Checks out both commits of a request into temporary worktrees, builds and analyzes them in parallel, and
    /// combines their facts with the hunks between them. The worktrees are removed afterward, also on failure.
    /// </summary>
    public static async Task<ChangesetResult> BuildAsync(ChangesetRequest request, SolutionAnalyzer analyze, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(request.Root);
        var top = Path.GetFullPath(Git.Run(root, "rev-parse", "--show-toplevel"));
        var relative = Path.GetRelativePath(top, root);
        var scratch = Path.Combine(Path.GetTempPath(), "archmap-" + Guid.NewGuid().ToString("N")[..12]);
        var added = new List<string>();
        try
        {
            var sides = new[] { ("base", request.BaseCommit), ("head", request.HeadCommit) };
            foreach (var (name, commit) in sides)
            {
                var dir = Path.Combine(scratch, name);
                Git.Run(top, "worktree", "add", "--detach", "--quiet", dir, commit);
                added.Add(dir);
            }
            var facts = await Task.WhenAll(sides.Select(s =>
                FactsAtAsync(request, Path.GetFullPath(Path.Combine(scratch, s.Item1, relative)), s.Item2, analyze, Prefix(progress, s.Item1), ct)));
            var changeset = ChangesetBuilder.Build(facts[0], facts[1], root, request.BaseCommit, request.HeadCommit);
            return new ChangesetResult(changeset, facts[0], facts[1]);
        }
        finally
        {
            foreach (var dir in added) Git.TryRun(top, "worktree", "remove", "--force", dir);
            DeleteQuietly(scratch);
        }
    }

    /// <summary>Builds and analyzes the solution in one checked-out revision, and returns the facts the map gives it.</summary>
    private static async Task<Facts> FactsAtAsync(ChangesetRequest request, string root, string commit, SolutionAnalyzer analyze, IProgress<string>? progress, CancellationToken ct)
    {
        var solution = Path.Combine(root, request.Map.Solution);
        if (!File.Exists(solution)) throw new IOException($"{request.Map.Solution} does not exist at {commit[..Math.Min(8, commit.Length)]}");
        if (request.Build)
        {
            progress?.Report("building");
            await SolutionBuild.RunAsync(solution, ct);
        }
        var analysis = await analyze(root, solution, progress, ct);
        foreach (var w in analysis.Warnings) progress?.Report("warning: " + w);
        var facts = Checker.Run(request.Map, analysis, new CheckOptions { Commit = commit, MapText = request.MapText, LayoutText = request.LayoutText });
        if (!facts.Passed) throw new InvalidOperationException("the map cannot be used at " + commit + ":\n  " + string.Join("\n  ", facts.Violations.Select(v => v.Message)));
        return facts;
    }

    private static IProgress<string>? Prefix(IProgress<string>? progress, string name) =>
        progress is null ? null : new PrefixedProgress(progress, name + ": ");

    /// <summary>Forwards each report to another progress sink at once, with a prefix naming the revision.</summary>
    private sealed class PrefixedProgress(IProgress<string> inner, string prefix) : IProgress<string>
    {
        public void Report(string value) => inner.Report(prefix + value);
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
