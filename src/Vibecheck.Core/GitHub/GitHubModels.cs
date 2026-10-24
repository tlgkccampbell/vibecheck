namespace Vibecheck.GitHub;

/// <summary>
/// Identifies a repository on GitHub.com or on a GitHub Enterprise Server: the host, the owner and the name, and the
/// repository's web address.
/// </summary>
public sealed record GitHubRepository(string Host, string Owner, string Name)
{
    /// <summary>Gets the repository's name with its owner, such as <c>octo/app</c>.</summary>
    public string NameWithOwner => Owner + "/" + Name;

    /// <summary>Gets a value indicating whether the repository is on GitHub.com rather than on an Enterprise Server.</summary>
    public bool IsDotCom => Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An open pull request as the picker lists it. The checks are the rollup state of the head commit's checks, such as
/// <c>SUCCESS</c>, <c>FAILURE</c> or <c>PENDING</c>, and null when it has none. The review decision is GitHub's, such
/// as <c>APPROVED</c>, <c>CHANGES_REQUESTED</c> or <c>REVIEW_REQUIRED</c>, and null when the repository requires none.
/// </summary>
/// <param name="ReviewRequested">A value indicating whether the pull request requests the viewer's review.</param>
public sealed record PullSummary(int Number, string Title, string Url, string? Author, bool IsDraft, string HeadOid, string BaseRefName,
    string? ReviewDecision, string? Checks, bool ReviewRequested);

/// <summary>
/// The state of one pull request at one moment, which the server sends to the viewer as a <c>pull</c> event. The state
/// is <c>OPEN</c>, <c>CLOSED</c> or <c>MERGED</c>, the merge state is GitHub's <c>mergeStateStatus</c>, such as
/// <c>BLOCKED</c> or <c>CLEAN</c>, and the checks and review decision are as <see cref="PullSummary"/> describes them.
/// </summary>
/// <param name="Commits">The number of commits the pull request holds.</param>
/// <param name="LatestReviewCommit">The commit the viewer's latest submitted review was made on, or null when the viewer has not reviewed.</param>
public sealed record PullSnapshot(int Number, string Title, string Url, string State, bool IsDraft, string? Author,
    string HeadRefName, string HeadOid, string BaseRefName, string BaseOid, string? ReviewDecision, string? MergeState, string? Checks,
    bool ViewerDidAuthor, int Commits, string? LatestReviewCommit, string? LatestReviewState)
{
    /// <summary>Gets the pull request's description, timeline, review threads and pending review, or <see langword="null"/> when they were not read.</summary>
    public PullDiscussion? Discussion { get; init; }
}

/// <summary>
/// Holds what people have said on a pull request: its timeline, oldest first and starting with the description, its
/// review threads, and the viewer's pending review. See <c>docs/pull-requests.md</c> sections 6 and 7.
/// </summary>
/// <param name="Pending">The viewer's pending review, which only the viewer can see, or null when the viewer has none.</param>
public sealed record PullDiscussion(IReadOnlyList<PullItem> Timeline, IReadOnlyList<ReviewThread> Threads, PendingReview? Pending);

/// <summary>
/// One entry of a pull request's timeline. The kind is <c>description</c>, <c>comment</c>, <c>review</c>,
/// <c>commit</c>, <c>force-push</c>, <c>base-changed</c>, <c>ready</c>, <c>draft</c>, <c>closed</c>, <c>reopened</c>
/// or <c>merged</c>. The body is GitHub's rendering of the entry's Markdown as HTML, which GitHub sanitizes.
/// </summary>
/// <param name="Author">The login of the person who wrote the entry or caused the event, or null for a deleted account.</param>
public sealed record PullItem(string Kind, string Id, string? Author, DateTimeOffset? At, string? Url)
{
    /// <summary>Gets the HTML of a description, comment or review, or <see langword="null"/> for any other entry.</summary>
    public string? BodyHtml { get; init; }

    /// <summary>Gets a review's state, such as <c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>COMMENTED</c> or <c>DISMISSED</c>.</summary>
    public string? State { get; init; }

    /// <summary>Gets the commit a review was made on, a pushed commit, the commit a force-push left, or the merge commit.</summary>
    public string? Commit { get; init; }

    /// <summary>Gets the headline of a pushed commit's message.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the commit a force-push replaced, or the base branch before a change of base.</summary>
    public string? From { get; init; }

    /// <summary>Gets the base branch after a change of base, or the branch a merge went into.</summary>
    public string? To { get; init; }
}

/// <summary>
/// A review thread: comments on a file of a pull request, or on lines of it. The side is <c>LEFT</c> for lines at base
/// and <c>RIGHT</c> for lines at head, and the line numbers are lines of the file at that side. A thread that is not
/// outdated has its line in the pull request's current diff. Every thread keeps the line it was written on, in the
/// file at <see cref="OriginalCommit"/>. The path is relative to the repository's top.
/// </summary>
/// <param name="Line">
/// The last line the thread is on in the current diff, or null when it is on the whole file or GitHub cannot place it.
/// GitHub can give an outdated thread a line too, which is not a line of the current diff.
/// </param>
/// <param name="StartLine">The first line of a thread on several lines, or null.</param>
/// <param name="OnFile">A value indicating whether the thread is on the whole file rather than on lines.</param>
public sealed record ReviewThread(string Id, string Path, string Side, int? Line, int? StartLine, int? OriginalLine, int? OriginalStartLine,
    bool OnFile, bool IsResolved, bool IsOutdated, string? ResolvedBy, string? OriginalCommit, IReadOnlyList<ThreadComment> Comments);

/// <summary>
/// One comment of a review thread. A pending comment belongs to the viewer's pending review and only the viewer can see
/// it. The diff hunk is the part of the diff the thread was written against, ending at its line.
/// </summary>
/// <param name="Review">The identifier of the review the comment belongs to, or null.</param>
public sealed record ThreadComment(string Id, string? Author, string BodyHtml, DateTimeOffset At, string Url, bool Pending, string? Review, string? DiffHunk);

/// <summary>The viewer's pending review: its identifier, which later writes add to, and how many comments it holds.</summary>
public sealed record PendingReview(string Id, int Comments);

/// <summary>A changed file of a pull request as GitHub lists it, with its path at base when GitHub reports it renamed. Paths are relative to the repository's top.</summary>
public sealed record PullFile(string Path, string Status, string? PreviousPath);

/// <summary>Reports that a request to GitHub failed, with GitHub's message when it gave one.</summary>
public class GitHubException(string message) : Exception(message);

/// <summary>Reports that GitHub asked for requests to stop for a while, as its secondary rate limit does.</summary>
public sealed class GitHubRateLimitException(string message, TimeSpan retryAfter) : GitHubException(message)
{
    /// <summary>Gets how long GitHub asked to wait before the next request.</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Reads pull requests from GitHub. <see cref="GitHubClient"/> is the real one, and tests answer from recorded
/// responses. See <c>docs/pull-requests.md</c> section 3.1.
/// </summary>
public interface IGitHub
{
    /// <summary>Gets the repository this client reads.</summary>
    GitHubRepository Repository { get; }

    /// <summary>Returns the repository's open pull requests, most recently updated first.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task<IReadOnlyList<PullSummary>> ListPullsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the current state of a pull request with its discussion, or <see langword="null"/> when the repository has
    /// no pull request with that number.
    /// </summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task<PullSnapshot?> GetPullAsync(int number, CancellationToken ct = default);

    /// <summary>Returns the changed files of a pull request as GitHub lists them.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task<IReadOnlyList<PullFile>> GetPullFilesAsync(int number, CancellationToken ct = default);
}
