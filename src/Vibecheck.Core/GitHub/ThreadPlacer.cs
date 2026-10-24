using Vibecheck.Diffing;
using Vibecheck.Reviewing;

namespace Vibecheck.GitHub;

/// <summary>
/// Locates a review thread in a review: the changeset path of its file, and the hunk and the line within the hunk that
/// it is shown under. A thread with no hunk is listed at the top of its file, as an outdated thread, a thread on the
/// whole file, or a thread whose line is not in this review's diff.
/// </summary>
/// <param name="Line">The index of the thread's line in <see cref="Hunk.Lines"/>, or null with a null hunk.</param>
public sealed record ThreadPlace(string Path, int? Hunk, int? Line);

/// <summary>
/// Places a pull request's review threads in the diffs of one of its reviews. See <c>docs/pull-requests.md</c>
/// section 7.1.
/// </summary>
public static class ThreadPlacer
{
    /// <summary>
    /// Returns where each thread on a file of the review's diff is shown, by thread identifier. Threads on files that the
    /// diff does not hold are left out, so they appear only in the conversation.
    /// </summary>
    /// <remarks>
    /// A thread that is not outdated has its line in the pull request's current diff. That line is in the review's diff
    /// at head when the review's head is the pull request's head, and at base when its base is the pull request's base
    /// too. A thread on the head side was also written on a line of the file at <see cref="ReviewThread.OriginalCommit"/>,
    /// so it keeps its place in a review of that commit after later pushes outdate it.
    /// </remarks>
    /// <param name="hunks">The changeset's hunks, whose paths are relative to <paramref name="root"/>.</param>
    /// <param name="root">The directory the changeset's paths are relative to, relative to the repository's top.</param>
    /// <param name="head">The review's head commit.</param>
    /// <param name="sameHead">A value indicating whether the review's head is the pull request's current head.</param>
    /// <param name="sameBase">A value indicating whether the review's base is the pull request's current base.</param>
    public static Dictionary<string, ThreadPlace> Place(IEnumerable<ReviewThread> threads, IEnumerable<FileHunks> hunks, string root, string head, bool sameHead, bool sameBase)
    {
        var byPath = hunks.ToDictionary(h => h.Path, h => h.Hunks, StringComparer.Ordinal);
        var result = new Dictionary<string, ThreadPlace>(StringComparer.Ordinal);
        foreach (var t in threads)
        {
            if (ReviewPipeline.ChangesetPath(t.Path, root) is not { } path || !byPath.TryGetValue(path, out var fileHunks)) continue;
            var right = t.Side != "LEFT";
            (int Hunk, int Line)? at = null;
            if (!t.OnFile && !t.IsOutdated && t.Line is { } line && sameHead && (right || sameBase)) at = Find(fileHunks, right, line);
            if (at is null && !t.OnFile && right && t.OriginalCommit == head && t.OriginalLine is { } original) at = Find(fileHunks, right, original);
            result[t.Id] = new ThreadPlace(path, at?.Hunk, at?.Line);
        }
        return result;
    }

    /// <summary>
    /// Returns the hunk and the index within it of a line of the file at head, or at base when <paramref name="right"/>
    /// is false, or null when no hunk shows that line. Lines are numbered from one, as GitHub numbers them.
    /// </summary>
    public static (int Hunk, int Line)? Find(IReadOnlyList<Hunk> hunks, bool right, int line)
    {
        for (var h = 0; h < hunks.Count; h++)
        {
            if (!hunks[h].TryRanges(out var old, out var now)) continue;
            var number = (right ? now.Start : old.Start) + 1;
            var lines = hunks[h].Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i][0] == (right ? "-" : "+")) continue;
                if (number == line) return (h, i);
                number++;
            }
        }
        return null;
    }
}
