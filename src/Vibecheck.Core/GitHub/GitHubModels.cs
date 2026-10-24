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

    /// <summary>Gets GitHub's node identifier of the pull request, which writes through GraphQL name it by.</summary>
    public string? NodeId { get; init; }

    /// <summary>Gets each check of the head commit, in GitHub's order, or <see langword="null"/> when they were not read.</summary>
    public IReadOnlyList<PullCheck>? CheckList { get; init; }
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

    /// <summary>Gets the address of the author's avatar on GitHub, or <see langword="null"/> when GitHub gives none.</summary>
    public string? Avatar { get; init; }

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
public sealed record ThreadComment(string Id, string? Author, string BodyHtml, DateTimeOffset At, string Url, bool Pending, string? Review, string? DiffHunk)
{
    /// <summary>Gets the address of the author's avatar on GitHub, or <see langword="null"/> when GitHub gives none.</summary>
    public string? Avatar { get; init; }
}

/// <summary>The viewer's pending review: its identifier, which later writes add to, and how many comments it holds.</summary>
public sealed record PendingReview(string Id, int Comments);

/// <summary>
/// One check of a pull request's head commit: a check run, such as a job of a GitHub Actions workflow, or a commit
/// status that another service set. The state is <c>success</c>, <c>failure</c>, <c>pending</c>, <c>cancelled</c>,
/// <c>skipped</c> or <c>neutral</c>, whichever kind the check is. See <c>docs/pull-requests.md</c> section 9.
/// </summary>
/// <param name="Workflow">The name of the workflow a GitHub Actions job belongs to, or null.</param>
/// <param name="Required">A value indicating whether the base branch's protection requires the check to pass before merging.</param>
/// <param name="Url">The check's page, on GitHub or on the service that set it.</param>
public sealed record PullCheck(string Id, string Name, string? Workflow, bool Required, string State, DateTimeOffset? Started, DateTimeOffset? Completed, string? Url)
{
    /// <summary>Gets the identifier of the GitHub Actions job that a check run is, whose log can be read, or <see langword="null"/> for any other check.</summary>
    public long? JobId { get; init; }

    /// <summary>Gets the description a commit status carries, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the annotations a check run left, at most the fifty the query reads.</summary>
    public IReadOnlyList<CheckAnnotation> Annotations { get; init; } = [];
}

/// <summary>
/// A note that a check run left on lines of the head commit. The level is <c>FAILURE</c>, <c>WARNING</c> or
/// <c>NOTICE</c>. The path is relative to the repository's top, and a workflow can name a path that is not a file,
/// such as <c>.github</c>.
/// </summary>
public sealed record CheckAnnotation(string Path, int StartLine, int EndLine, string Level, string Message, string? Title);

/// <summary>
/// Locates a comment in a pull request's diff as GitHub's API takes it: the path relative to the repository's top, and
/// the side and line number of the comment's last line. A comment on several lines also has the side and line of its
/// first line. The side is <c>LEFT</c> for lines at base and <c>RIGHT</c> for lines at head. See
/// <c>docs/pull-requests.md</c> section 8.1.
/// </summary>
public sealed record DiffAnchor(string Path, string Side, int Line, string? StartSide = null, int? StartLine = null);

/// <summary>A changed file of a pull request as GitHub lists it, with its path at base when GitHub reports it renamed. Paths are relative to the repository's top.</summary>
public sealed record PullFile(string Path, string Status, string? PreviousPath);

/// <summary>Reports that a request to GitHub failed, with GitHub's message when it gave one.</summary>
public class GitHubException(string message) : Exception(message)
{
    /// <summary>Gets the HTTP status GitHub answered with, or <see langword="null"/> when it did not answer or answered a query with errors.</summary>
    public System.Net.HttpStatusCode? Status { get; init; }
}

/// <summary>Reports that GitHub asked for requests to stop for a while, as its secondary rate limit does.</summary>
public sealed class GitHubRateLimitException(string message, TimeSpan retryAfter) : GitHubException(message)
{
    /// <summary>Gets how long GitHub asked to wait before the next request.</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Reads pull requests from GitHub and writes comments and reviews to them as the viewer. <see cref="GitHubClient"/> is
/// the real one, and tests answer from recorded responses. See <c>docs/pull-requests.md</c> section 3.1.
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

    /// <summary>Adds a comment to a pull request's conversation, given the pull request's node identifier.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task AddCommentAsync(string pullId, string body, CancellationToken ct = default);

    /// <summary>Posts a comment on lines of a pull request's diff at once, outside any review, anchored to a commit.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task AddSingleCommentAsync(int number, string commit, DiffAnchor at, string body, CancellationToken ct = default);

    /// <summary>Creates the viewer's pending review on a pull request, anchored to a commit, and returns its identifier.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task<string> StartReviewAsync(string pullId, string commit, CancellationToken ct = default);

    /// <summary>Adds a comment on lines of the diff to the viewer's pending review, which starts a new thread.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task AddReviewThreadAsync(string reviewId, DiffAnchor at, string body, CancellationToken ct = default);

    /// <summary>Replies to a review thread, as part of the viewer's pending review when one is given and at once otherwise.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task ReplyAsync(string threadId, string body, string? reviewId, CancellationToken ct = default);

    /// <summary>Resolves a review thread, or unresolves it.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task ResolveAsync(string threadId, bool resolved, CancellationToken ct = default);

    /// <summary>
    /// Submits the viewer's pending review with an event and a summary, or creates and submits a review anchored to a
    /// commit in one call when there is no pending review. The event is <c>COMMENT</c>, <c>APPROVE</c> or
    /// <c>REQUEST_CHANGES</c>.
    /// </summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task SubmitReviewAsync(string pullId, string? reviewId, string commit, string @event, string body, CancellationToken ct = default);

    /// <summary>Deletes the viewer's pending review with its comments.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task DeleteReviewAsync(string reviewId, CancellationToken ct = default);

    /// <summary>Returns the log of a GitHub Actions job.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task<string> GetJobLogAsync(long jobId, CancellationToken ct = default);

    /// <summary>Returns Markdown rendered as HTML the way GitHub renders it in the repository.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task<string> RenderMarkdownAsync(string text, CancellationToken ct = default);
}
