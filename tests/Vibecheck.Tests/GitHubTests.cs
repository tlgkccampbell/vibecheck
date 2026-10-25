using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vibecheck.Cli;
using Vibecheck.Diffing;
using Vibecheck.GitHub;
using Xunit;

namespace Vibecheck.Tests;

public class GitHubTests
{
    private static readonly GitHubRepository Repo = new("github.com", "cli", "cli");

    /// <summary>Returns a fixture recorded from GitHub's responses, from <c>GitHub/</c> beside the tests.</summary>
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GitHub", name));

    private static GitHubClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer) => new(Repo, "token", new StubHandler(answer));

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ListsOpenPullRequestsWithTheirChecksAndWhetherTheViewersReviewIsRequested()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var client = Client(r => { sent = r; body = r.Content!.ReadAsStringAsync().Result; return Json(Fixture("pulls.json")); });

        var pulls = await client.ListPullsAsync();

        Assert.Equal("https://api.github.com/graphql", sent!.RequestUri!.ToString());
        Assert.Equal("bearer token", sent.Headers.Authorization!.ToString());
        Assert.Equal(GitHubClient.Query("pulls.graphql"), JsonNode.Parse(body!)!["query"]!.GetValue<string>());
        Assert.Equal([13894, 12942, 13247], pulls.Select(p => p.Number));
        Assert.Equal([false, true, false], pulls.Select(p => p.ReviewRequested));
        var first = pulls[0];
        Assert.Equal(("user-1", true, "trunk", "REVIEW_REQUIRED", "SUCCESS"), (first.Author, first.IsDraft, first.BaseRefName, first.ReviewDecision, first.Checks));
        Assert.Equal("8f44336ccb889465115aa85319c275c8dec1c3b7", first.HeadOid);
    }

    [Fact]
    public async Task ReadsAPullRequestsSnapshotWithItsDiscussion()
    {
        using var client = Client(_ => Json(Fixture("discussion.json")));
        var pull = (await client.GetPullAsync(14663))!;
        Assert.Equal(("MERGED", false, 23, false), (pull.State, pull.IsDraft, pull.Commits, pull.ViewerDidAuthor));
        Assert.Equal(("153adc66eef2af5fe51715ea7f571f3514c50bec", "316e7a80fba9ba68b45cb55e0ade5ab707a54cbd"), (pull.HeadOid, pull.BaseOid));
        Assert.Null(pull.LatestReviewCommit);
        Assert.StartsWith("PR_", pull.NodeId);

        // The description comes first, and the timeline follows in GitHub's order.
        var discussion = pull.Discussion!;
        Assert.Equal(("description", pull.Author), (discussion.Timeline[0].Kind, discussion.Timeline[0].Author));
        Assert.StartsWith("<h3", discussion.Timeline[0].BodyHtml);
        // The description's Markdown comes too, for its intent section.
        Assert.StartsWith("### Description\r\n", pull.Body);
        var kinds = discussion.Timeline.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int> { ["description"] = 1, ["review"] = 3, ["force-push"] = 7, ["commit"] = 23, ["merged"] = 1, ["closed"] = 1 }, kinds);
        var review = discussion.Timeline.First(i => i.Kind == "review");
        Assert.Equal(("COMMENTED", "f7b5fbd69df429ae391d5a926856b359821860fd"), (review.State, review.Commit));
        Assert.Equal(DateTimeOffset.Parse("2026-10-13T16:38:24Z"), review.At);
        var merged = discussion.Timeline.Single(i => i.Kind == "merged");
        Assert.Equal(("4a289a9e19cac88013626378185f8debb690a59c", "trunk"), (merged.Commit, merged.To));
        Assert.All(discussion.Timeline.Where(i => i.Kind == "force-push"), i => Assert.True(i.From is { Length: 40 } && i.Commit is { Length: 40 }));
        Assert.All(discussion.Timeline.Where(i => i.Kind == "commit"), i => Assert.True(i.Commit is { Length: 40 } && !string.IsNullOrEmpty(i.Text)));

        // Every entry and comment has its author's avatar, a pushed commit by its git identity's when it has no account.
        Assert.All(discussion.Timeline, i => Assert.StartsWith("https://avatars.githubusercontent.com/", i.Avatar));
        Assert.All(discussion.Threads.SelectMany(t => t.Comments), c => Assert.StartsWith("https://avatars.githubusercontent.com/", c.Avatar));

        Assert.Null(discussion.Pending);
        Assert.Equal(9, discussion.Threads.Count);
        Assert.Equal(2, discussion.Threads.Count(t => t.IsOutdated));
        // GitHub can give an outdated thread a line, which the placer does not trust.
        Assert.Contains(discussion.Threads, t => t.IsOutdated && t.Line is not null);
        var thread = discussion.Threads[^1];
        Assert.Equal(("internal/config/refreshable.go", "RIGHT", 426, 426, false), (thread.Path, thread.Side, thread.Line, thread.OriginalLine, thread.OnFile));
        Assert.Equal("24d96ddc578040649c4c406e0ad458ae2986e239", thread.OriginalCommit);
        Assert.Equal(["PRR_kwDODKw3uc8AAAABSMvzVg", "PRR_kwDODKw3uc8AAAABTDatBw"], thread.Comments.Select(c => c.Review));
        Assert.All(thread.Comments, c => Assert.True(!c.Pending && c.DiffHunk!.StartsWith("@@") && c.BodyHtml.Length > 0));
    }

    [Fact]
    public async Task ReadsEveryPageOfThreadsAndOfTheTimeline()
    {
        // The first answer says that more threads follow. The second, asked for the threads alone, holds the last of them.
        var first = JsonNode.Parse(Fixture("discussion.json"))!;
        var pr = first["data"]!["repository"]!["pullRequest"]!;
        pr["reviewThreads"]!["pageInfo"] = new JsonObject { ["hasNextPage"] = true, ["endCursor"] = "T1" };
        var second = first.DeepClone();
        var rest = second["data"]!["repository"]!["pullRequest"]!.AsObject();
        rest.Remove("timelineItems");
        var extra = pr["reviewThreads"]!["nodes"]![0]!.DeepClone();
        extra["id"] = "PRRT_next";
        rest["reviewThreads"] = new JsonObject { ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = "T2" }, ["nodes"] = new JsonArray(extra) };
        var bodies = new List<JsonNode>();
        using var client = Client(r =>
        {
            bodies.Add(JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!);
            return Json((bodies.Count == 1 ? first : second).ToJsonString());
        });

        var pull = (await client.GetPullAsync(14663))!;

        Assert.Equal(2, bodies.Count);
        var variables = bodies[1]["variables"]!;
        Assert.Equal((true, "T1", false), (variables["threads"]!.GetValue<bool>(), variables["threadsAfter"]!.GetValue<string>(), variables["timeline"]!.GetValue<bool>()));
        Assert.Equal(10, pull.Discussion!.Threads.Count);
        Assert.Equal("PRRT_next", pull.Discussion.Threads[^1].Id);
        Assert.Equal(36, pull.Discussion.Timeline.Count);
    }

    /// <summary>
    /// Returns the hunks of one file, in which line 11 at base became lines 11 and 12 at head, between line 10 and
    /// lines 12 and 13 at base, which are lines 13 and 14 at head.
    /// </summary>
    private static List<FileHunks> OneHunk(string path) =>
        [new FileHunks(path, [new Hunk("@@ -10,4 +10,5 @@ class C", [[" ", "a"], ["-", "b"], ["+", "B"], ["+", "B2"], [" ", "c"], [" ", "d"]])])];

    private static ReviewThread Thread(string id, string path = "src/C.cs", string side = "RIGHT", int? line = 12, int? original = 12, bool outdated = false, bool onFile = false, string? commit = "h0") =>
        new(id, path, side, line, null, original, null, onFile, false, outdated, null, commit, []);

    [Fact]
    public void ThreadsLandOnTheirLinesAndTheRestAtTheTopOfTheirFile()
    {
        var threads = new[]
        {
            Thread("right"),
            Thread("left", side: "LEFT", line: 11, original: 11),
            Thread("context", line: 14, original: 14),
            Thread("beyond", line: 40, original: 40),
            Thread("outdated", line: 12, original: 13, outdated: true, commit: "old"),
            Thread("kept", line: null, original: 11, outdated: true, commit: "h1"),
            Thread("file", line: null, original: null, onFile: true),
            Thread("elsewhere", path: "docs/readme.md"),
            Thread("untouched", path: "src/D.cs"),
        };
        var top = new ThreadPlace("C.cs", null, null);

        // The review is of the pull request's current range, at h1.
        var current = ThreadPlacer.Place(threads, OneHunk("C.cs"), "src", "h1", sameHead: true, sameBase: true);
        Assert.Equal(new ThreadPlace("C.cs", 0, 3), current["right"]);
        Assert.Equal(new ThreadPlace("C.cs", 0, 1), current["left"]);
        Assert.Equal(new ThreadPlace("C.cs", 0, 5), current["context"]);
        Assert.Equal(top, current["beyond"]);
        Assert.Equal(top, current["outdated"]);
        // An outdated thread written on the review's head keeps its line there.
        Assert.Equal(new ThreadPlace("C.cs", 0, 2), current["kept"]);
        Assert.Equal(top, current["file"]);
        // A thread outside the review's directory, or on a file its diff does not hold, is only in the conversation.
        Assert.False(current.ContainsKey("elsewhere"));
        Assert.False(current.ContainsKey("untouched"));

        // A review from a later base shares the head's lines but not the base's.
        var since = ThreadPlacer.Place(threads, OneHunk("C.cs"), "src", "h1", sameHead: true, sameBase: false);
        Assert.Equal(current["right"], since["right"]);
        Assert.Equal(top, since["left"]);

        // A review of an earlier head shows only the threads written on that head, on the lines they were written on.
        var earlier = ThreadPlacer.Place(threads, OneHunk("C.cs"), "src", "h0", sameHead: false, sameBase: false);
        Assert.Equal(new ThreadPlace("C.cs", 0, 3), earlier["right"]);
        Assert.Equal(top, earlier["kept"]);
        Assert.Equal(top, earlier["left"]);
    }

    [Fact]
    public void LinesAreNumberedFromOneOnTheirSide()
    {
        Assert.Equal((0, 0), ThreadPlacer.Find([new Hunk("@@ -0,0 +1,2 @@", [["+", "x"], ["+", "y"]])], right: true, 1));
        Assert.Equal((1, 1), ThreadPlacer.Find([new Hunk("@@ -1 +1 @@", [["-", "a"], ["+", "b"]]), new Hunk("@@ -9,2 +9,2 @@", [[" ", "c"], ["-", "d"], ["+", "e"]])], right: false, 10));
        Assert.Null(ThreadPlacer.Find([new Hunk("@@ -1 +1 @@", [["-", "a"], ["+", "b"]])], right: true, 2));
    }

    [Fact]
    public async Task WritesSendTheRequestsGitHubExpects()
    {
        var sent = new List<(string Method, string Uri, JsonNode? Body)>();
        using var client = Client(r =>
        {
            var text = r.Content?.ReadAsStringAsync().Result;
            var body = text is null ? null : JsonNode.Parse(text);
            sent.Add((r.Method.Method, r.RequestUri!.ToString(), body));
            if (r.RequestUri.AbsolutePath == "/markdown") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<p><strong>hi</strong></p>", Encoding.UTF8, "text/html") };
            if (body?["operationName"]?.GetValue<string>() == "StartReview") return Json("""{"data":{"addPullRequestReview":{"pullRequestReview":{"id":"PRR_new"}}}}""");
            return Json("""{"data":{}}""");
        });
        var line = new DiffAnchor("app/Thing.cs", "RIGHT", 12);
        var lines = new DiffAnchor("app/Thing.cs", "LEFT", 13, "LEFT", 11);

        await client.AddCommentAsync("PR_1", "hello");
        await client.AddSingleCommentAsync(7, "abc", lines, "on lines");
        Assert.Equal("PRR_new", await client.StartReviewAsync("PR_1", "abc"));
        await client.AddReviewThreadAsync("PRR_new", line, "in the review");
        await client.ReplyAsync("T1", "reply", "PRR_new");
        await client.ResolveAsync("T1", resolved: false);
        await client.SubmitReviewAsync("PR_1", "PRR_new", "abc", "REQUEST_CHANGES", "please");
        await client.SubmitReviewAsync("PR_1", null, "abc", "APPROVE", "");
        await client.DeleteReviewAsync("PRR_new");
        Assert.Equal("<p><strong>hi</strong></p>", await client.RenderMarkdownAsync("**hi**"));

        // Each GraphQL write names its mutation in the embedded document and passes its variables.
        var graphql = sent.Where(s => s.Uri == "https://api.github.com/graphql").Select(s => s.Body!).ToList();
        Assert.All(graphql, b => Assert.Equal(GitHubClient.Query("write.graphql"), b["query"]!.GetValue<string>()));
        string Vars(JsonNode b) => b["operationName"] + " " + b["variables"]!.ToJsonString();
        Assert.Equal(
        [
            """AddComment {"subject":"PR_1","body":"hello"}""",
            """StartReview {"pull":"PR_1","commit":"abc"}""",
            """AddThread {"review":"PRR_new","path":"app/Thing.cs","body":"in the review","line":12,"side":"RIGHT","startLine":null,"startSide":null}""",
            """Reply {"thread":"T1","body":"reply","review":"PRR_new"}""",
            """Unresolve {"thread":"T1"}""",
            """Submit {"review":"PRR_new","event":"REQUEST_CHANGES","body":"please"}""",
            """CreateAndSubmit {"pull":"PR_1","commit":"abc","event":"APPROVE","body":""}""",
            """Discard {"review":"PRR_new"}""",
        ], graphql.Select(Vars));

        // A single comment on lines and a Markdown preview go through REST.
        var single = sent.Single(s => s.Uri.EndsWith("/pulls/7/comments"));
        Assert.Equal(("POST", "https://api.github.com/repos/cli/cli/pulls/7/comments"), (single.Method, single.Uri));
        Assert.Equal("""{"body":"on lines","commit_id":"abc","path":"app/Thing.cs","line":13,"side":"LEFT","start_line":11,"start_side":"LEFT"}""", single.Body!.ToJsonString());
        var markdown = sent.Single(s => s.Uri == "https://api.github.com/markdown");
        Assert.Equal("""{"text":"**hi**","mode":"gfm","context":"cli/cli"}""", markdown.Body!.ToJsonString());
    }

    [Fact]
    public async Task AWriteGitHubRefusesReportsItsMessage()
    {
        using var client = Client(_ => Json("""{"data":{"addPullRequestReview":null},"errors":[{"message":"User can only have one pending review per pull request"}]}"""));
        var ex = await Assert.ThrowsAsync<GitHubException>(() => client.StartReviewAsync("PR_1", "abc"));
        Assert.Contains("User can only have one pending review per pull request", ex.Message);
    }

    [Fact]
    public void ALineOrRangeOfAHunkMapsToTheLineAndSideGitHubTakesAndBack()
    {
        var hunks = OneHunk("C.cs")[0].Hunks;
        Assert.Equal(new DiffAnchor("src/C.cs", "RIGHT", 12), ThreadPlacer.Anchor("src/C.cs", hunks, 0, 3, 3, right: true));
        Assert.Equal(new DiffAnchor("src/C.cs", "LEFT", 11), ThreadPlacer.Anchor("src/C.cs", hunks, 0, 1, 1, right: false));
        // A context line has a number on each side.
        Assert.Equal(new DiffAnchor("src/C.cs", "LEFT", 12), ThreadPlacer.Anchor("src/C.cs", hunks, 0, 4, 4, right: false));
        Assert.Equal(new DiffAnchor("src/C.cs", "RIGHT", 13, "RIGHT", 11), ThreadPlacer.Anchor("src/C.cs", hunks, 0, 2, 4, right: true));
        Assert.Throws<ArgumentException>(() => ThreadPlacer.Anchor("src/C.cs", hunks, 0, 2, 2, right: false));
        Assert.Throws<ArgumentException>(() => ThreadPlacer.Anchor("src/C.cs", hunks, 0, 1, 1, right: true));
        Assert.Throws<ArgumentException>(() => ThreadPlacer.Anchor("src/C.cs", hunks, 0, 4, 2, right: true));
        Assert.Throws<ArgumentException>(() => ThreadPlacer.Anchor("src/C.cs", hunks, 1, 0, 0, right: true));

        // Every line placed as a comment is found again where it was written.
        for (var i = 0; i < hunks[0].Lines.Count; i++)
            foreach (var right in new[] { true, false })
                if (hunks[0].Lines[i][0] != (right ? "-" : "+"))
                    Assert.Equal((0, i), ThreadPlacer.Find(hunks, right, ThreadPlacer.Anchor("C.cs", hunks, 0, i, i, right).Line));

        // The changeset's paths are relative to the review's directory, and GitHub's to the repository's top.
        Assert.Equal("src/C.cs", Vibecheck.Reviewing.ReviewPipeline.RepositoryPath("C.cs", "src"));
        Assert.Equal("C.cs", Vibecheck.Reviewing.ReviewPipeline.ChangesetPath(Vibecheck.Reviewing.ReviewPipeline.RepositoryPath("C.cs", "src"), "src"));
        Assert.Equal("C.cs", Vibecheck.Reviewing.ReviewPipeline.RepositoryPath("C.cs", "."));
    }

    [Fact]
    public async Task ReadsTheHeadCommitsChecksWithTheirAnnotations()
    {
        using var client = Client(_ => Json(Fixture("checks.json")));
        var checks = (await client.GetPullAsync(13788))!.CheckList!;

        Assert.Equal(11, checks.Count);
        var failed = checks.Where(c => c.State == "failure").ToList();
        Assert.Equal(["build (ubuntu-latest)", "build (windows-latest)", "build (macos-latest)"], failed.Select(c => c.Name));
        Assert.All(failed, c => Assert.True(c.Required && c.Workflow == "Unit and Integration Tests" && c.Url!.StartsWith("https://github.com/")));
        Assert.Equal(84988385122, failed[0].JobId);
        Assert.Equal(new CheckAnnotation(".github", 347, 347, "FAILURE", "Process completed with exit code 1.", null), failed[0].Annotations.Single() with { Title = null });
        Assert.True(failed[0].Completed > failed[0].Started);
        // A check run that GitHub Actions did not run has no job whose log can be read.
        var codeql = checks.Single(c => c.Name == "CodeQL");
        Assert.Equal(("success", null, null), (codeql.State, codeql.JobId, codeql.Workflow));
    }

    [Fact]
    public async Task ChecksOfEveryKindHaveOneOfTheSameStates()
    {
        // A run in progress, a cancelled run, and commit statuses that failed and that wait, beside the recorded checks.
        var doc = JsonNode.Parse(Fixture("checks.json"))!;
        var nodes = doc["data"]!["repository"]!["pullRequest"]!["lastCommit"]!["nodes"]![0]!["commit"]!["statusCheckRollup"]!["contexts"]!["nodes"]!.AsArray();
        nodes[1]!["status"] = "IN_PROGRESS";
        nodes[1]!["conclusion"] = null;
        nodes[2]!["conclusion"] = "CANCELLED";
        nodes[6]!["conclusion"] = "TIMED_OUT";
        nodes.Add(JsonNode.Parse("""{"__typename":"StatusContext","id":"SC_1","context":"ci/external","state":"ERROR","description":"Build errored","targetUrl":"https://ci.example/1","createdAt":"2026-10-01T00:00:00Z","isRequired":true}"""));
        nodes.Add(JsonNode.Parse("""{"__typename":"StatusContext","id":"SC_2","context":"ci/slow","state":"EXPECTED","description":null,"targetUrl":null,"createdAt":"2026-10-01T00:00:00Z","isRequired":false}"""));
        using var client = Client(_ => Json(doc.ToJsonString()));
        var checks = (await client.GetPullAsync(13788))!.CheckList!;
        Assert.Equal(["failure", "pending", "cancelled", "failure"], new[] { 0, 1, 2, 6 }.Select(i => checks[i].State));
        Assert.Equal(("failure", true, "Build errored", (long?)null), (checks[^2].State, checks[^2].Required, checks[^2].Description, checks[^2].JobId));
        Assert.Equal("pending", checks[^1].State);
    }

    [Fact]
    public async Task ReadsTheEndOfAJobsLogWithoutTimesOrColors()
    {
        string? path = null;
        using var client = Client(r =>
        {
            path = r.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("﻿2026-10-01T10:00:00.1234567Z ##[group]Run tests\r\n2026-10-01T10:00:01.0000000Z \u001b[31mFAIL\u001b[0m TestThing\r\n2026-10-01T10:00:02.0000000Z ##[error]Process completed with exit code 1.\r\n\r\n") };
        });
        var log = await client.GetJobLogAsync(84988385122);
        Assert.Equal("/repos/cli/cli/actions/jobs/84988385122/logs", path);
        Assert.Equal(["##[group]Run tests", "FAIL TestThing", "##[error]Process completed with exit code 1."], JobLogs.Tail(log, 60));
        Assert.Equal(["##[error]Process completed with exit code 1."], JobLogs.Tail(log, 1));

        // GitHub answers 410 for a log it has deleted after the retention period.
        using var expired = Client(_ => Json("""{"message":"Server Error","status":"410"}""", HttpStatusCode.Gone));
        var gone = await Assert.ThrowsAsync<GitHubException>(() => expired.GetJobLogAsync(1));
        Assert.Contains("no longer keeps this job's log", gone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnnotationsLandOnTheHeadsLinesOnlyInAReviewOfThatHead()
    {
        var check = new PullCheck("CR_1", "build", "CI", true, "failure", null, null, null)
        {
            Annotations =
            [
                new CheckAnnotation("src/C.cs", 12, 12, "FAILURE", "CS1002: ; expected", null),
                new CheckAnnotation("src/C.cs", 40, 40, "WARNING", "unused", null),
                new CheckAnnotation(".github", 347, 347, "FAILURE", "Process completed with exit code 1.", null),
            ],
        };
        var current = ThreadPlacer.PlaceAnnotations([check], OneHunk("C.cs"), "src", sameHead: true);
        Assert.Equal(new ThreadPlace("C.cs", 0, 3), current["CR_1/0"]);
        Assert.Equal(new ThreadPlace("C.cs", null, null), current["CR_1/1"]);
        Assert.False(current.ContainsKey("CR_1/2"));
        var earlier = ThreadPlacer.PlaceAnnotations([check], OneHunk("C.cs"), "src", sameHead: false);
        Assert.Equal(new ThreadPlace("C.cs", null, null), earlier["CR_1/0"]);
    }

    [Fact]
    public async Task ListsAPullRequestsFilesPageByPageWithRenames()
    {
        // GitHub's REST schema gives a renamed file its earlier path as previous_filename.
        var recorded = JsonNode.Parse(Fixture("files.json"))!.AsArray();
        var full = new JsonArray(Enumerable.Range(0, 100).Select(i => (JsonNode)new JsonObject { ["filename"] = $"f{i}.go", ["status"] = "modified" }).ToArray());
        recorded.Add(new JsonObject { ["filename"] = "new/name.go", ["status"] = "renamed", ["previous_filename"] = "old/name.go" });
        var pages = new List<string>();
        using var client = Client(r =>
        {
            pages.Add(r.RequestUri!.Query);
            return Json(r.RequestUri.Query.Contains("page=1&") || r.RequestUri.Query.EndsWith("page=1") ? full.ToJsonString() : recorded.ToJsonString());
        });

        var files = await client.GetPullFilesAsync(13894);

        Assert.Equal(["?per_page=100&page=1", "?per_page=100&page=2"], pages);
        Assert.Equal(104, files.Count);
        Assert.Equal(new PullFile("new/name.go", "renamed", "old/name.go"), files[^1]);
        Assert.Equal("internal/skills/discovery/discovery.go", files[100].Path);
    }

    [Fact]
    public async Task ReportsARateLimitWithTheWaitGitHubAsksForAndAQueryError()
    {
        using var limited = Client(_ =>
        {
            var r = Json("""{"message":"You have exceeded a secondary rate limit."}""", HttpStatusCode.Forbidden);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return r;
        });
        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => limited.GetPullAsync(1));
        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);

        using var refused = Client(_ => Json("""{"errors":[{"message":"Could not resolve to a Repository."}]}"""));
        var error = await Assert.ThrowsAsync<GitHubException>(() => refused.ListPullsAsync());
        Assert.Contains("Could not resolve to a Repository.", error.Message);
    }

    [Fact]
    public void FindsTheRemoteThatPointsAtTheRepositoryOverHttpsOrSsh()
    {
        using var repo = new TempRepo();
        repo.Git("remote", "add", "origin", "https://github.com/me/fork.git");
        repo.Git("remote", "add", "upstream", "git@github.com:Cli/CLI.git");
        Assert.Equal("upstream", PullCommits.RemoteOf(repo.Path, Repo));
        Assert.Equal("origin", PullCommits.RemoteOf(repo.Path, new GitHubRepository("github.com", "someone", "else")));
    }

    [Fact]
    public void FetchingAPullRequestWritesNoRef()
    {
        using var upstream = new TempRepo();
        upstream.Write("a.txt", "1\n");
        upstream.Commit("one");
        using var clone = new TempRepo();
        clone.Git("remote", "add", "origin", upstream.Path);
        clone.Git("fetch", "--quiet", "origin");
        clone.Git("checkout", "--quiet", "-b", "main", "origin/main");
        // The pull request's head lives only under refs/pull, as GitHub keeps it, and the base moves on after the clone.
        upstream.Git("checkout", "--quiet", "-b", "feature");
        upstream.Write("b.txt", "2\n");
        var head = upstream.Commit("feature");
        upstream.Git("update-ref", "refs/pull/7/head", head);
        upstream.Git("checkout", "--quiet", "main");
        upstream.Git("branch", "--quiet", "-D", "feature");
        upstream.Write("a.txt", "3\n");
        var baseTip = upstream.Commit("base moves");
        var refs = clone.Git("for-each-ref");
        var pull = new PullSnapshot(7, "t", "u", "OPEN", false, null, "feature", head, "main", baseTip, null, null, null, false, 1, null, null);
        Assert.False(PullCommits.Present(clone.Path, pull));

        PullCommits.Fetch(clone.Path, "origin", 7, "main");

        Assert.True(PullCommits.Present(clone.Path, pull));
        Assert.Equal(refs, clone.Git("for-each-ref"));
    }

    [Fact]
    public void ThePullRequestsRangeStartsAtTheMergeBaseWithGitHubsBaseNotALocalRef()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "1\n");
        var a = repo.Commit("a");
        repo.Write("a.txt", "2\n");
        var b = repo.Commit("b");
        repo.Git("checkout", "--quiet", "-b", "feature");
        repo.Write("f.txt", "f\n");
        var head = repo.Commit("feature");
        repo.Git("checkout", "--quiet", "main");
        repo.Write("a.txt", "3\n");
        var c = repo.Commit("c");
        // The reviewer's main is stale: it still points at a.
        repo.Git("update-ref", "refs/heads/main", a);
        var pull = new PullSnapshot(1, "t", "u", "OPEN", false, null, "feature", head, "main", c, null, null, null, false, 1, null, null);
        Assert.Equal((b, head), PullCommits.Range(repo.Path, pull));
    }

    [Fact]
    public void RenamesArePairedAsGitHubPairsThem()
    {
        using var repo = new TempRepo();
        repo.Write("Small.cs", "class Small { int A; }\n");
        repo.Write("Moved.cs", string.Join("\n", Enumerable.Range(1, 10).Select(i => $"// line {i}")) + "\n");
        var before = repo.Commit("one");
        File.Delete(Path.Combine(repo.Path, "Small.cs"));
        repo.Write("Tiny.cs", "class Tiny { long B; string C; }\n");
        File.Move(Path.Combine(repo.Path, "Moved.cs"), Path.Combine(repo.Path, "Elsewhere.cs"));
        var after = repo.Commit("two");
        var git = GitHunks.Changes(repo.Path, before, after);
        Assert.Contains(git, c => c.Path == "Elsewhere.cs" && c.Status == "renamed");
        Assert.DoesNotContain(git, c => c.Path == "Tiny.cs" && c.Status == "renamed");

        // GitHub pairs Small.cs with Tiny.cs, which git finds too different, and does not pair Moved.cs with Elsewhere.cs.
        var changes = GitHunks.Reconcile(git, new Dictionary<string, string> { ["Tiny.cs"] = "Small.cs" });
        Assert.Equal(["Elsewhere.cs added", "Moved.cs removed", "Tiny.cs renamed"], changes.Select(c => $"{c.Path} {c.Status}"));
        Assert.Equal("Small.cs", changes.Single(c => c.Path == "Tiny.cs").PreviousPath);

        var hunks = GitHunks.Collect(repo.Path, before, after, changes).ToDictionary(f => f.Path);
        Assert.Equal([["-", "class Small { int A; }"], ["+", "class Tiny { long B; string C; }"]], hunks["Tiny.cs"].Hunks.SelectMany(h => h.Lines));
        Assert.All(hunks["Elsewhere.cs"].Hunks.SelectMany(h => h.Lines), l => Assert.Equal("+", l[0]));
        Assert.All(hunks["Moved.cs"].Hunks.SelectMany(h => h.Lines), l => Assert.Equal("-", l[0]));
        Assert.False(hunks.ContainsKey("Small.cs"));
    }

    [Fact]
    public void APullRequestIsNamedByItsNumberOrItsAddress()
    {
        Assert.Equal(42, Program.PullNumber("42", null));
        Assert.Equal(42, Program.PullNumber("#42", null));
        Assert.Equal(42, Program.PullNumber("https://github.com/cli/cli/pull/42/files", null));
        using var client = new GitHubClient(Repo, "token");
        Assert.Equal(42, Program.PullNumber("https://github.com/CLI/cli/pull/42", client));
        Assert.Throws<ArgumentException>(() => Program.PullNumber("https://github.com/other/repo/pull/42", client));
        Assert.Throws<ArgumentException>(() => Program.PullNumber("main", client));
    }

    /// <summary>Answers every request with a function, so that the real client is exercised without a network.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer(request));
    }
}
