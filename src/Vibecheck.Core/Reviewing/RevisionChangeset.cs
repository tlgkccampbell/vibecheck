using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Model;

namespace Vibecheck.Reviewing;

/// <summary>Analyzes a solution checked out at some path. <see cref="RoslynAnalyzer.AnalyzeAsync"/> is the real one.</summary>
public delegate Task<AnalysisResult> SolutionAnalyzer(string root, string solution, IProgress<string>? progress, CancellationToken ct);

/// <summary>
/// Describes one changeset to produce: the directory inside the repository that the map's paths are relative to, the
/// map that describes both ends, the layout beside it, the two commits, and how to prepare each before analysis.
/// </summary>
public sealed record ChangesetRequest(string Root, ArchitectureMap Map, string MapText, string? LayoutText, string BaseCommit, string HeadCommit, SolutionPreparation Preparation = SolutionPreparation.Restore);

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
    /// request does. An empty side of a range, or a missing head, means <c>HEAD</c>. <see cref="WorkingTree.Name"/>
    /// names a snapshot of the uncommitted work, and on its own means the uncommitted work against <c>HEAD</c>.
    /// </summary>
    public static (string Base, string Head) ResolveRange(string dir, string first, string? second = null)
    {
        if (second is not null) return (Commit(dir, first), Commit(dir, second));
        if (first == WorkingTree.Name) return (Commit(dir, "HEAD"), Commit(dir, first));
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

    private static string Commit(string dir, string rev) => rev == WorkingTree.Name ? WorkingTree.Snapshot(dir) :
        Git.TryRun(dir, "rev-parse", "--verify", "--quiet", rev + "^{commit}") ?? throw new ArgumentException($"\"{rev}\" does not name a commit");

    /// <summary>
    /// Checks out both commits of a request into temporary worktrees, prepares and analyzes them in parallel, and
    /// combines their facts with the hunks between them. The worktrees are removed afterward, also on failure. With a
    /// cache, an end analyzed before is read from it instead.
    /// </summary>
    public static async Task<ChangesetResult> BuildAsync(ChangesetRequest request, SolutionAnalyzer analyze, IProgress<string>? progress = null, CancellationToken ct = default, ReviewCache? cache = null)
    {
        var root = Path.GetFullPath(request.Root);
        var top = Path.GetFullPath(Git.Run(root, "rev-parse", "--show-toplevel"));
        var target = new AnalysisTarget(Path.GetRelativePath(top, root), request.Map.Solution, request.Preparation);
        var sides = new[] { ("base", request.BaseCommit), ("head", request.HeadCommit) };
        var analyses = await Task.WhenAll(sides.Select(s => AnalyzeAsync(top, target, s.Item2, analyze, cache, Prefix(progress, s.Item1), ct)));
        var facts = sides.Select((s, i) => Check(request.Map, request.MapText, request.LayoutText, analyses[i], s.Item2)).ToArray();
        var changeset = ChangesetBuilder.Build(facts[0], facts[1], root, request.BaseCommit, request.HeadCommit);
        return new ChangesetResult(changeset, facts[0], facts[1]);
    }

    /// <summary>
    /// Returns the analysis of one commit. A cached analysis is returned when there is one. Otherwise the commit is
    /// checked out into a temporary worktree, restored or built as the target asks, and analyzed, and the worktree is
    /// removed afterward, also on failure.
    /// </summary>
    public static Task<AnalysisResult> AnalyzeAsync(string top, AnalysisTarget target, string commit, SolutionAnalyzer analyze, ReviewCache? cache, IProgress<string>? progress, CancellationToken ct)
    {
        if (cache is null) return CheckOutAndAnalyzeAsync(top, target, commit, analyze, progress, ct);
        return cache.AnalysisAsync(target, commit, () => CheckOutAndAnalyzeAsync(top, target, commit, analyze, progress, ct), progress);
    }

    private static async Task<AnalysisResult> CheckOutAndAnalyzeAsync(string top, AnalysisTarget target, string commit, SolutionAnalyzer analyze, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("checking out " + commit[..Math.Min(8, commit.Length)]);
        using var worktree = Worktree.Add(top, commit);
        var root = Path.GetFullPath(Path.Combine(worktree.Path, target.Root));
        var solution = Path.Combine(root, target.Solution);
        if (!File.Exists(solution)) throw new IOException($"{target.Solution} does not exist at {commit[..Math.Min(8, commit.Length)]}");
        if (target.Preparation != SolutionPreparation.None)
        {
            progress?.Report(target.Preparation == SolutionPreparation.Build ? "building" : "restoring");
            await SolutionBuild.RunAsync(solution, target.Preparation, ct);
        }
        var analysis = await analyze(root, solution, progress, ct);
        foreach (var w in analysis.Warnings) progress?.Report("warning: " + w);
        return analysis;
    }

    /// <summary>Applies a map to the analysis of one commit and returns the facts, throwing when the map cannot be used there.</summary>
    public static Facts Check(ArchitectureMap map, string? mapText, string? layoutText, AnalysisResult analysis, string commit)
    {
        var facts = Checker.Run(map, analysis, new CheckOptions { Commit = commit, MapText = mapText, LayoutText = layoutText });
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
}
