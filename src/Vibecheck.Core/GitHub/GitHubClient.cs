using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Vibecheck.GitHub;

/// <summary>
/// Reads and writes pull requests on GitHub over HTTPS, as the reviewer, with a token the server holds in memory. It
/// reads through GraphQL, which returns a pull request's state in one query, and lists files through REST. It writes
/// through GraphQL, except single comments on lines and Markdown previews, which only REST offers. The queries and
/// mutations are embedded from <c>GitHub/Queries</c>. See <c>docs/pull-requests.md</c> sections 3.1 and 8.
/// </summary>
public sealed class GitHubClient : IGitHub, IDisposable
{
    private readonly HttpClient http;
    private readonly Uri api, graphql;

    /// <summary>Creates a client for a repository with a token, sending its requests through a handler when one is given.</summary>
    public GitHubClient(GitHubRepository repository, string token, HttpMessageHandler? handler = null)
    {
        Repository = repository;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("vibecheck", "0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        (api, graphql) = repository.IsDotCom
            ? (new Uri("https://api.github.com/"), new Uri("https://api.github.com/graphql"))
            : (new Uri($"https://{repository.Host}/api/v3/"), new Uri($"https://{repository.Host}/api/graphql"));
    }

    public GitHubRepository Repository { get; }

    public async Task<IReadOnlyList<PullSummary>> ListPullsAsync(CancellationToken ct = default)
    {
        var data = await QueryAsync("pulls.graphql", new { owner = Repository.Owner, name = Repository.Name }, ct);
        var viewer = Str(data.GetProperty("viewer"), "login");
        var result = new List<PullSummary>();
        foreach (var pr in data.GetProperty("repository").GetProperty("pullRequests").GetProperty("nodes").EnumerateArray())
        {
            var requested = pr.GetProperty("reviewRequests").GetProperty("nodes").EnumerateArray()
                .Any(r => r.GetProperty("requestedReviewer") is { ValueKind: JsonValueKind.Object } who && Str(who, "login") == viewer);
            result.Add(new PullSummary(pr.GetProperty("number").GetInt32(), Str(pr, "title")!, Str(pr, "url")!, Login(pr), pr.GetProperty("isDraft").GetBoolean(),
                Str(pr, "headRefOid")!, Str(pr, "baseRefName")!, Str(pr, "reviewDecision"), Checks(pr.GetProperty("commits")), requested));
        }
        return result;
    }

    public async Task<PullSnapshot?> GetPullAsync(int number, CancellationToken ct = default)
    {
        var variables = new Dictionary<string, object?> { ["owner"] = Repository.Owner, ["name"] = Repository.Name, ["number"] = number };
        var data = await QueryAsync("pull.graphql", variables, ct);
        if (data.GetProperty("repository").GetProperty("pullRequest") is not { ValueKind: JsonValueKind.Object } pr) return null;
        var nodeId = Str(pr, "id");
        var checks = CheckList(pr.GetProperty("lastCommit"));
        var latest = pr.GetProperty("viewerLatestReview");
        var reviewed = latest.ValueKind == JsonValueKind.Object;
        var timeline = new List<PullItem> { new("description", "description", Login(pr), Time(pr, "createdAt"), Str(pr, "url")) { BodyHtml = Str(pr, "bodyHTML"), Avatar = Avatar(pr, "author") } };
        var threads = new List<ReviewThread>();
        var pending = pr.GetProperty("pending").GetProperty("nodes").EnumerateArray()
            .Select(r => new PendingReview(Str(r, "id")!, r.GetProperty("comments").GetProperty("totalCount").GetInt32())).FirstOrDefault();
        var snapshot = new PullSnapshot(pr.GetProperty("number").GetInt32(), Str(pr, "title")!, Str(pr, "url")!, Str(pr, "state")!, pr.GetProperty("isDraft").GetBoolean(), Login(pr),
            Str(pr, "headRefName")!, Str(pr, "headRefOid")!, Str(pr, "baseRefName")!, Str(pr, "baseRefOid")!, Str(pr, "reviewDecision"), Str(pr, "mergeStateStatus"),
            Checks(pr.GetProperty("lastCommit")), pr.GetProperty("viewerDidAuthor").GetBoolean(), pr.GetProperty("commits").GetProperty("totalCount").GetInt32(),
            reviewed && latest.GetProperty("commit") is { ValueKind: JsonValueKind.Object } c ? Str(c, "oid") : null, reviewed ? Str(latest, "state") : null);

        // GitHub returns at most a hundred threads and a hundred timeline entries at a time, so the query runs again for
        // each connection that has more, from where it left off, until neither has.
        while (true)
        {
            var moreThreads = Page(pr, "reviewThreads", n => threads.Add(Thread(n)), out var threadsAfter);
            var moreTimeline = Page(pr, "timelineItems", n => { if (Item(n) is { } item) timeline.Add(item); }, out var timelineAfter);
            if (!moreThreads && !moreTimeline) break;
            (variables["threads"], variables["threadsAfter"], variables["timeline"], variables["timelineAfter"]) = (moreThreads, threadsAfter, moreTimeline, timelineAfter);
            data = await QueryAsync("pull.graphql", variables, ct);
            if (data.GetProperty("repository").GetProperty("pullRequest") is not { ValueKind: JsonValueKind.Object } next) break;
            pr = next;
        }
        return snapshot with { Discussion = new PullDiscussion(timeline, threads, pending), NodeId = nodeId, CheckList = checks };
    }

    /// <summary>
    /// Passes each node of a connection to an action and reports whether the connection has more, with the cursor to
    /// continue from. A connection the query left out has nothing and no more.
    /// </summary>
    private static bool Page(JsonElement pr, string connection, Action<JsonElement> add, out string? after)
    {
        after = null;
        if (!pr.TryGetProperty(connection, out var page) || page.ValueKind != JsonValueKind.Object) return false;
        foreach (var n in page.GetProperty("nodes").EnumerateArray()) add(n);
        var info = page.GetProperty("pageInfo");
        after = Str(info, "endCursor");
        return info.GetProperty("hasNextPage").GetBoolean() && after is not null;
    }

    /// <summary>Reads a review thread with its comments. A thread holds at most the hundred comments the query reads.</summary>
    private static ReviewThread Thread(JsonElement t)
    {
        var comments = t.GetProperty("comments").GetProperty("nodes").EnumerateArray().Select(c => new ThreadComment(Str(c, "id")!, Login(c), Str(c, "bodyHTML") ?? "",
            Time(c, "createdAt") ?? default, Str(c, "url")!, Str(c, "state") == "PENDING", Obj(c, "pullRequestReview") is { } r ? Str(r, "id") : null, Str(c, "diffHunk")) { Avatar = Avatar(c, "author") }).ToList();
        var first = t.GetProperty("comments").GetProperty("nodes").EnumerateArray().FirstOrDefault();
        return new ReviewThread(Str(t, "id")!, Str(t, "path")!, Str(t, "diffSide") ?? "RIGHT", Int(t, "line"), Int(t, "startLine"), Int(t, "originalLine"), Int(t, "originalStartLine"),
            Str(t, "subjectType") == "FILE", t.GetProperty("isResolved").GetBoolean(), t.GetProperty("isOutdated").GetBoolean(), Obj(t, "resolvedBy") is { } by ? Str(by, "login") : null,
            Obj(first, "originalCommit") is { } oc ? Str(oc, "oid") : null, comments);
    }

    /// <summary>Reads one timeline entry with its author's avatar, or returns null for a review that is still pending, which the timeline does not show.</summary>
    private static PullItem? Item(JsonElement n)
    {
        var item = Entry(n);
        if (item is null) return null;
        // A commit's author is a git identity, which has an avatar of its own when it matches no GitHub account.
        var commitAuthor = Obj(Obj(n, "commit"), "author");
        return item with { Avatar = Avatar(n, "author") ?? Avatar(n, "actor") ?? Avatar(commitAuthor, "user") ?? (commitAuthor is { } c ? Str(c, "avatarUrl") : null) };
    }

    /// <summary>Reads one timeline entry without its avatar, or returns null for a pending review or an entry of another kind.</summary>
    private static PullItem? Entry(JsonElement n)
    {
        var id = Str(n, "id")!;
        var actor = Obj(n, "actor") is { } a ? Str(a, "login") : null;
        var at = Time(n, "createdAt");
        string? Oid(string name) => Obj(n, name) is { } o ? Str(o, "oid") : null;
        switch (Str(n, "__typename"))
        {
            case "IssueComment":
                return new PullItem("comment", id, Login(n), at, Str(n, "url")) { BodyHtml = Str(n, "bodyHTML") };
            case "PullRequestReview":
                if (Str(n, "state") == "PENDING") return null;
                return new PullItem("review", id, Login(n), Time(n, "submittedAt") ?? at, Str(n, "url")) { BodyHtml = Str(n, "bodyHTML"), State = Str(n, "state"), Commit = Oid("commit") };
            case "PullRequestCommit":
                var commit = n.GetProperty("commit");
                var author = Obj(commit, "author");
                var who = Obj(author, "user") is { } user ? Str(user, "login") : Str(author ?? default, "name");
                return new PullItem("commit", id, who, Time(commit, "committedDate"), Str(n, "url")) { Commit = Str(commit, "oid"), Text = Str(commit, "messageHeadline") };
            case "HeadRefForcePushedEvent":
                return new PullItem("force-push", id, actor, at, null) { From = Oid("beforeCommit"), Commit = Oid("afterCommit") };
            case "BaseRefChangedEvent":
                return new PullItem("base-changed", id, actor, at, null) { From = Str(n, "previousRefName"), To = Str(n, "currentRefName") };
            case "MergedEvent":
                return new PullItem("merged", id, actor, at, null) { Commit = Oid("commit"), To = Str(n, "mergeRefName") };
            case "ReadyForReviewEvent": return new PullItem("ready", id, actor, at, null);
            case "ConvertToDraftEvent": return new PullItem("draft", id, actor, at, null);
            case "ClosedEvent": return new PullItem("closed", id, actor, at, null);
            case "ReopenedEvent": return new PullItem("reopened", id, actor, at, null);
            default: return null;
        }
    }

    public async Task<IReadOnlyList<PullFile>> GetPullFilesAsync(int number, CancellationToken ct = default)
    {
        var result = new List<PullFile>();
        // GitHub lists at most 3,000 files, a hundred to a page.
        for (var page = 1; page <= 30; page++)
        {
            using var doc = await SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(api, $"repos/{Repository.Owner}/{Repository.Name}/pulls/{number}/files?per_page=100&page={page}")), ct);
            var files = doc.RootElement.EnumerateArray().ToList();
            foreach (var f in files) result.Add(new PullFile(Str(f, "filename")!, Str(f, "status")!, Str(f, "previous_filename")));
            if (files.Count < 100) break;
        }
        return result;
    }

    public Task AddCommentAsync(string pullId, string body, CancellationToken ct = default) =>
        MutateAsync("AddComment", new() { ["subject"] = pullId, ["body"] = body }, ct);

    public async Task AddSingleCommentAsync(int number, string commit, DiffAnchor at, string body, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, object> { ["body"] = body, ["commit_id"] = commit, ["path"] = at.Path, ["line"] = at.Line, ["side"] = at.Side };
        if (at.StartLine is { } start) (fields["start_line"], fields["start_side"]) = (start, at.StartSide ?? at.Side);
        using var _ = await SendAsync(Post($"repos/{Repository.Owner}/{Repository.Name}/pulls/{number}/comments", fields), ct);
    }

    public async Task<string> StartReviewAsync(string pullId, string commit, CancellationToken ct = default)
    {
        var data = await MutateAsync("StartReview", new() { ["pull"] = pullId, ["commit"] = commit }, ct);
        return Str(data.GetProperty("addPullRequestReview").GetProperty("pullRequestReview"), "id") ?? throw new GitHubException("GitHub did not return the new review.");
    }

    public Task AddReviewThreadAsync(string reviewId, DiffAnchor at, string body, CancellationToken ct = default) =>
        MutateAsync("AddThread", new()
        {
            ["review"] = reviewId, ["path"] = at.Path, ["body"] = body, ["line"] = at.Line, ["side"] = at.Side,
            ["startLine"] = at.StartLine, ["startSide"] = at.StartLine is null ? null : at.StartSide ?? at.Side,
        }, ct);

    public Task ReplyAsync(string threadId, string body, string? reviewId, CancellationToken ct = default) =>
        MutateAsync("Reply", new() { ["thread"] = threadId, ["body"] = body, ["review"] = reviewId }, ct);

    public Task ResolveAsync(string threadId, bool resolved, CancellationToken ct = default) =>
        MutateAsync(resolved ? "Resolve" : "Unresolve", new() { ["thread"] = threadId }, ct);

    public Task SubmitReviewAsync(string pullId, string? reviewId, string commit, string @event, string body, CancellationToken ct = default) =>
        reviewId is not null
            ? MutateAsync("Submit", new() { ["review"] = reviewId, ["event"] = @event, ["body"] = body }, ct)
            : MutateAsync("CreateAndSubmit", new() { ["pull"] = pullId, ["commit"] = commit, ["event"] = @event, ["body"] = body }, ct);

    public Task DeleteReviewAsync(string reviewId, CancellationToken ct = default) =>
        MutateAsync("Discard", new() { ["review"] = reviewId }, ct);

    public async Task<string> GetJobLogAsync(long jobId, CancellationToken ct = default)
    {
        try
        {
            return await SendTextAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(api, $"repos/{Repository.Owner}/{Repository.Name}/actions/jobs/{jobId}/logs")), ct);
        }
        catch (GitHubException ex) when (ex.Status == HttpStatusCode.Gone)
        {
            // GitHub deletes logs after the repository's retention period, 90 days unless it is set otherwise.
            throw new GitHubException("GitHub no longer keeps this job's log; it deletes logs after the repository's retention period.") { Status = ex.Status };
        }
    }

    public Task<string> RenderMarkdownAsync(string text, CancellationToken ct = default) =>
        SendTextAsync(Post("markdown", new Dictionary<string, object> { ["text"] = text, ["mode"] = "gfm", ["context"] = Repository.NameWithOwner }), ct);

    public void Dispose() => http.Dispose();

    /// <summary>Runs one of the mutations embedded in <c>write.graphql</c> and returns its <c>data</c>.</summary>
    private Task<JsonElement> MutateAsync(string operation, Dictionary<string, object?> variables, CancellationToken ct) =>
        QueryAsync("write.graphql", variables, ct, operation);

    /// <summary>Returns a REST request that posts fields as JSON to a path under the API's address.</summary>
    private HttpRequestMessage Post(string path, Dictionary<string, object> fields) =>
        new(HttpMethod.Post, new Uri(api, path)) { Content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json") };

    /// <summary>
    /// Runs an embedded GraphQL document with variables, or the named operation of a document that holds several, and
    /// returns its <c>data</c>, throwing when GitHub reports an error.
    /// </summary>
    private async Task<JsonElement> QueryAsync(string name, object variables, CancellationToken ct, string? operation = null)
    {
        var body = JsonSerializer.Serialize(new { query = Query(name), variables, operationName = operation });
        var request = new HttpRequestMessage(HttpMethod.Post, graphql) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var doc = await SendAsync(request, ct);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            throw new GitHubException("GitHub refused the query: " + Str(errors[0], "message"));
        return doc.RootElement.GetProperty("data").Clone();
    }

    /// <summary>Sends a request and returns the parsed answer, throwing on a refusal, with the wait GitHub asks for when it limits the rate.</summary>
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct) => JsonDocument.Parse(await SendTextAsync(request, ct));

    /// <summary>Sends a request and returns the text of the answer, throwing on a refusal, with the wait GitHub asks for when it limits the rate.</summary>
    private async Task<string> SendTextAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException($"GitHub could not be reached: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubException("GitHub took too long to answer.");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) return text;
            var message = Message(text) ?? response.ReasonPhrase ?? response.StatusCode.ToString();
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden && RetryAfter(response) is { } wait)
                throw new GitHubRateLimitException($"GitHub limited the rate of requests: {message}", wait);
            throw new GitHubException($"GitHub answered {(int)response.StatusCode}: {message}") { Status = response.StatusCode };
        }
    }

    /// <summary>Returns the wait a rate-limited response asks for, from <c>Retry-After</c> or the reset time, or null when it is not one.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0"
            && response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var epoch))
            return TimeSpan.FromSeconds(Math.Max(1, epoch - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return null;
    }

    private static string? Message(string text)
    {
        try { using var doc = JsonDocument.Parse(text); return Str(doc.RootElement, "message"); }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads each check of the last commit in a <c>commits(last: 1)</c> connection, which is empty when it has none.</summary>
    private static List<PullCheck> CheckList(JsonElement commits)
    {
        var result = new List<PullCheck>();
        foreach (var n in commits.GetProperty("nodes").EnumerateArray())
        {
            if (Obj(n.GetProperty("commit"), "statusCheckRollup") is not { } rollup || Obj(rollup, "contexts") is not { } contexts) continue;
            foreach (var c in contexts.GetProperty("nodes").EnumerateArray())
            {
                var required = c.TryGetProperty("isRequired", out var r) && r.ValueKind == JsonValueKind.True;
                if (Str(c, "__typename") == "StatusContext")
                {
                    result.Add(new PullCheck(Str(c, "id")!, Str(c, "context")!, null, required, StatusState(Str(c, "state")), Time(c, "createdAt"), null, Str(c, "targetUrl")) { Description = Str(c, "description") });
                    continue;
                }
                if (Str(c, "__typename") != "CheckRun") continue;
                var suite = Obj(c, "checkSuite");
                var actions = Obj(suite, "app") is { } app && Str(app, "name") == "GitHub Actions";
                var workflow = Obj(Obj(suite, "workflowRun"), "workflow") is { } w ? Str(w, "name") : null;
                var annotations = Obj(c, "annotations") is { } a
                    ? a.GetProperty("nodes").EnumerateArray().Select(x => new CheckAnnotation(Str(x, "path")!, Line(x, "start"), Line(x, "end"), Str(x, "annotationLevel") ?? "NOTICE", Str(x, "message") ?? "", Str(x, "title"))).ToList()
                    : [];
                result.Add(new PullCheck(Str(c, "id")!, Str(c, "name")!, workflow, required, RunState(Str(c, "status"), Str(c, "conclusion")), Time(c, "startedAt"), Time(c, "completedAt"), Str(c, "detailsUrl"))
                {
                    JobId = actions && c.TryGetProperty("databaseId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null,
                    Annotations = annotations,
                });
            }
        }
        return result;
    }

    /// <summary>Returns the line where an annotation's location starts or ends.</summary>
    private static int Line(JsonElement annotation, string end) =>
        Obj(Obj(annotation, "location"), end) is { } at && Int(at, "line") is { } line ? line : 1;

    /// <summary>Returns the state of a check run: pending until it completes, and then what its conclusion says.</summary>
    private static string RunState(string? status, string? conclusion) => status != "COMPLETED" ? "pending" : conclusion switch
    {
        "SUCCESS" => "success",
        "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" or "ACTION_REQUIRED" => "failure",
        "CANCELLED" => "cancelled",
        "SKIPPED" => "skipped",
        _ => "neutral",
    };

    /// <summary>Returns the state of a commit status.</summary>
    private static string StatusState(string? state) => state switch
    {
        "SUCCESS" => "success",
        "FAILURE" or "ERROR" => "failure",
        _ => "pending",
    };

    /// <summary>Returns the rollup state of the checks of the last commit in a <c>commits(last: 1)</c> connection, or null when it has none.</summary>
    private static string? Checks(JsonElement commits) =>
        commits.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("commit").GetProperty("statusCheckRollup"))
            .Where(r => r.ValueKind == JsonValueKind.Object).Select(r => Str(r, "state")).FirstOrDefault();

    /// <summary>Returns the avatar address of the actor an element names under a property, such as its author, or null.</summary>
    private static string? Avatar(JsonElement? e, string property) => Obj(e, property) is { } who ? Str(who, "avatarUrl") : null;

    private static string? Login(JsonElement pr) => pr.GetProperty("author") is { ValueKind: JsonValueKind.Object } a ? Str(a, "login") : null;

    private static JsonElement? Obj(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static DateTimeOffset? Time(JsonElement e, string name) => Str(e, name) is { } s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Returns one of the GraphQL queries embedded in the library.</summary>
    public static string Query(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("github/" + name) ?? throw new FileNotFoundException("the query is not embedded", name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
