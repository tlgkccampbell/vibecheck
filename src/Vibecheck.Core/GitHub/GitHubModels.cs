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
    bool ViewerDidAuthor, int Commits, string? LatestReviewCommit, string? LatestReviewState);

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

    /// <summary>Returns the current state of a pull request, or <see langword="null"/> when the repository has no pull request with that number.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task<PullSnapshot?> GetPullAsync(int number, CancellationToken ct = default);

    /// <summary>Returns the changed files of a pull request as GitHub lists them.</summary>
    /// <exception cref="GitHubException">GitHub refused the request or could not be reached.</exception>
    Task<IReadOnlyList<PullFile>> GetPullFilesAsync(int number, CancellationToken ct = default);
}
