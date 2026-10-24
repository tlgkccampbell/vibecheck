using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vibecheck.Assist;
using Vibecheck.Cli;
using Vibecheck.Format;
using Vibecheck.GitHub;
using Vibecheck.Reviewing;
using Xunit;

namespace Vibecheck.Tests;

public class ReviewServerTests
{
    [Fact]
    public void ListsTheBranchAgainstMainAndTheRecentCommitsButNotTheRootCommit()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "1");
        var root = repo.Commit("root");
        repo.Git("checkout", "--quiet", "-b", "feature");
        repo.Write("a.txt", "2");
        var second = repo.Commit("second");
        repo.Git("checkout", "--quiet", "-b", "other", "main");
        repo.Write("b.txt", "3");
        repo.Commit("third");
        repo.Git("checkout", "--quiet", "feature");

        var ranges = RangeCatalog.List(repo.Path);

        Assert.Equal(new RangeChoice("branch", "feature against main", "main...HEAD", "1 commit"), ranges[0]);
        Assert.Equal([$"{second}^..{second}"], ranges.Where(r => r.Group == "commit").Select(r => r.Range));
        Assert.Equal(["main...other"], ranges.Where(r => r.Group == "branches").Select(r => r.Range));
        Assert.DoesNotContain(ranges, r => r.Range.Contains(root, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusesARequestWithoutTheTokenOrWithAnotherHost()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "1");
        repo.Commit("one");
        await using var server = new ReviewServer(Options(repo));
        var link = await server.StartAsync();

        using var bare = new HttpClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.GetAsync(server.BaseAddress + "/api/ranges")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.GetAsync(server.BaseAddress + "/api/ranges?token=wrong")).StatusCode);

        using var rebound = new HttpRequestMessage(HttpMethod.Get, link);
        rebound.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.SendAsync(rebound)).StatusCode);

        using var client = Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(link)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(server.BaseAddress + "/api/ranges")).StatusCode);
        using var form = new StringContent("range=HEAD", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync(server.BaseAddress + "/api/reviews", form)).StatusCode);
        // Every method that writes must send JSON, not only POST.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.DeleteAsync(server.BaseAddress + "/api/reviews/x/reviewed")).StatusCode);
    }

    [Fact]
    public async Task RunsAReviewAndServesTheViewerOverItsChangeset()
    {
        using var repo = new TempRepo();
        ReviewPipelineTests.WriteApp(repo, "class Thing { }\n");
        repo.Write("app/vibecheck.json", MapWriter.Write(TestData.Map()));
        var before = repo.Commit("one");
        ReviewPipelineTests.WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var after = repo.Commit("two");
        await using var server = new ReviewServer(Options(repo) with { Directory = Path.Combine(repo.Path, "app") });
        using var client = Client();
        await client.GetAsync(await server.StartAsync());

        var begun = await client.PostAsJsonAsync(server.BaseAddress + "/api/reviews", new { range = $"{before}..{after}" });
        var url = (await begun.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString();
        Assert.Equal($"/reviews/{before[..7]}-{after[..7]}/", url);

        var events = await ReadEventsAsync(client, $"{server.BaseAddress}/api/reviews/{before[..7]}-{after[..7]}/events");
        Assert.Equal("review", events[0].GetProperty("type").GetString());
        Assert.Equal("ready", events[^1].GetProperty("type").GetString());

        var page = await client.GetStringAsync(server.BaseAddress + url);
        Assert.StartsWith("<!doctype html>", page, StringComparison.Ordinal);
        Assert.Contains("<script src=\"changeset.js\"></script>", page, StringComparison.Ordinal);
        var script = await client.GetStringAsync(server.BaseAddress + url + "changeset.js");
        Assert.Contains("\"mapKind\":\"repository\"", script, StringComparison.Ordinal);
        Assert.Contains("window.CHANGESET = {", script, StringComparison.Ordinal);
        Assert.Contains("(function () {", await client.GetStringAsync(server.BaseAddress + url + "app.js"), StringComparison.Ordinal);
        Assert.Contains("\"reviewed\":{}", script, StringComparison.Ordinal);

        var api = $"{server.BaseAddress}/api/reviews/{before[..7]}-{after[..7]}";
        var marked = await client.PostAsJsonAsync(api + "/reviewed", new { paths = new[] { TestData.X1 }, reviewed = true });
        var marks = (await marked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("marks");
        Assert.Equal("reviewer", marks.GetProperty(TestData.X1).GetString());
        var announced = await ReadEventsAsync(client, api + "/events", e => e.GetProperty("type").GetString() == "reviewed");
        Assert.Equal("reviewer", announced[^1].GetProperty("marks").GetProperty(TestData.X1).GetString());
        Assert.Contains("\"reviewed\":{\"" + TestData.X1 + "\":\"reviewer\"}", await client.GetStringAsync(server.BaseAddress + url + "changeset.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns a pull request's snapshot with a head and a base. Its base branch is <c>main</c>, it holds the
    /// commits given, and its other fields are fixed.
    /// </summary>
    private static PullSnapshot Snapshot(int number, string head, string baseTip, string title = "Call the service", int commits = 1) =>
        new(number, title, $"https://github.com/o/r/pull/{number}", "OPEN", false, "author", "feature", head, "main", baseTip, "REVIEW_REQUIRED", "BLOCKED", "SUCCESS", false, commits, null, null);

    [Fact]
    public async Task OpensAPullRequestFetchingItsCommitsAndFollowsItWhileItsPageIsOpen()
    {
        // The pull request's head lives only in the upstream's refs/pull, as on GitHub, so opening it must fetch it.
        using var upstream = new TempRepo();
        ReviewPipelineTests.WriteApp(upstream, "class Thing { }\n");
        upstream.Write("app/vibecheck.json", MapWriter.Write(TestData.Map()));
        var baseTip = upstream.Commit("one");
        upstream.Git("checkout", "--quiet", "-b", "feature");
        ReviewPipelineTests.WriteApp(upstream, "class Thing { void Go() => Service.Run(); }\n");
        var head = upstream.Commit("two");
        upstream.Git("update-ref", "refs/pull/5/head", head);
        upstream.Git("checkout", "--quiet", "main");
        upstream.Git("branch", "--quiet", "-D", "feature");
        using var clone = new TempRepo();
        clone.Git("remote", "add", "origin", upstream.Path);
        clone.Git("fetch", "--quiet", "origin");
        clone.Git("checkout", "--quiet", "-b", "main", "origin/main");
        var refs = clone.Git("for-each-ref");

        var github = new FakeGitHub
        {
            Pulls = [new PullSummary(9, "Other", "u", "x", false, baseTip, "main", null, null, false), new PullSummary(5, "Call the service", "u", "author", false, head, "main", "REVIEW_REQUIRED", "SUCCESS", true)],
            Snapshots = { [5] = Snapshot(5, head, baseTip) },
        };
        await using var server = new ReviewServer(Options(clone) with { Directory = Path.Combine(clone.Path, "app"), GitHub = github, PollInterval = TimeSpan.FromMilliseconds(100) });
        using var client = Client();
        await client.GetAsync(await server.StartAsync());

        var ranges = await client.GetFromJsonAsync<JsonElement>(server.BaseAddress + "/api/ranges");
        var pulls = ranges.GetProperty("pulls").EnumerateArray().ToList();
        Assert.Equal([5, 9], pulls.Select(p => p.GetProperty("number").GetInt32()));
        Assert.Equal([false, true], pulls.Select(p => p.GetProperty("local").GetBoolean()));

        var begun = await client.PostAsJsonAsync(server.BaseAddress + "/api/reviews", new { pull = 5 });
        var id = (await begun.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal($"pr5-{head[..7]}", id);
        var api = $"{server.BaseAddress}/api/reviews/{id}";
        var events = await ReadEventsAsync(client, api + "/events");
        Assert.Equal("ready", events[^1].GetProperty("type").GetString());
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "stage" && e.GetProperty("stage").GetString() == "fetch" && e.GetProperty("message").GetString()!.StartsWith("fetching #5"));
        // The base is unknown until the commits are present, and is then the merge base with GitHub's base.
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "review" && e.TryGetProperty("base", out var b) && b.GetString() == baseTip);
        Assert.Equal(refs, clone.Git("for-each-ref"));
        var script = await client.GetStringAsync($"{server.BaseAddress}/reviews/{id}/changeset.js");
        Assert.Contains("\"pullCommits\":1", script, StringComparison.Ordinal);
        Assert.Contains($"\"headOid\":\"{head}\"", script, StringComparison.Ordinal);

        // While the page is open the server polls, and sends a snapshot only when it changed, or word that it is stale.
        github.Snapshots[5] = Snapshot(5, head, baseTip, title: "Call the service, renamed");
        var renamed = await ReadEventsAsync(client, api + "/events", e => e.TryGetProperty("snapshot", out var p) && p.GetProperty("title").GetString()!.EndsWith("renamed"));
        github.Fail = new GitHubRateLimitException("slow down", TimeSpan.FromMilliseconds(300));
        var stale = await ReadEventsAsync(client, api + "/events", e => e.TryGetProperty("stale", out _));
        Assert.Equal("slow down", stale[^1].GetProperty("message").GetString());
        Assert.Equal(2, stale.Count(e => e.TryGetProperty("snapshot", out _)));
        Assert.True(github.Reads >= 3);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(api + "/pull")).StatusCode);
    }

    [Fact]
    public async Task AttachesARangeWhoseHeadIsAPullRequestsHead()
    {
        using var repo = new TempRepo();
        ReviewPipelineTests.WriteApp(repo, "class Thing { }\n");
        repo.Write("app/vibecheck.json", MapWriter.Write(TestData.Map()));
        var before = repo.Commit("one");
        ReviewPipelineTests.WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var after = repo.Commit("two");
        var github = new FakeGitHub
        {
            Pulls = [new PullSummary(3, "Call", "u", "author", false, after, "main", null, null, false)],
            Snapshots = { [3] = Snapshot(3, after, before) },
        };
        await using var server = new ReviewServer(Options(repo) with { Directory = Path.Combine(repo.Path, "app"), GitHub = github });
        using var client = Client();
        await client.GetAsync(await server.StartAsync());
        await client.PostAsJsonAsync(server.BaseAddress + "/api/reviews", new { range = $"{before}..{after}" });

        var events = await ReadEventsAsync(client, $"{server.BaseAddress}/api/reviews/{before[..7]}-{after[..7]}/events");
        Assert.Contains(events, e => e.TryGetProperty("snapshot", out var p) && p.GetProperty("number").GetInt32() == 3);
    }

    [Fact]
    public async Task WithoutGitHubThePickerSaysWhyAndAPullRequestCannotOpen()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "1");
        repo.Commit("one");
        await using var server = new ReviewServer(Options(repo) with { GitHubNotice = "gh is missing" });
        using var client = Client();
        await client.GetAsync(await server.StartAsync());
        var ranges = await client.GetFromJsonAsync<JsonElement>(server.BaseAddress + "/api/ranges");
        Assert.Equal("gh is missing", ranges.GetProperty("github").GetString());
        Assert.Empty(ranges.GetProperty("pulls").EnumerateArray());
        var refused = await client.PostAsJsonAsync(server.BaseAddress + "/api/reviews", new { pull = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("gh is missing", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    /// <summary>Answers as GitHub would from pull requests held in memory, counting the snapshots it is asked for.</summary>
    private sealed class FakeGitHub : IGitHub
    {
        public GitHubRepository Repository { get; } = new("github.com", "o", "r");
        public List<PullSummary> Pulls { get; init; } = [];
        public Dictionary<int, PullSnapshot> Snapshots { get; } = [];
        public List<PullFile> Files { get; init; } = [];
        public Exception? Fail { get; set; }
        public int Reads;

        public Task<IReadOnlyList<PullSummary>> ListPullsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PullSummary>>(Pulls);

        public Task<PullSnapshot?> GetPullAsync(int number, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            if (Fail is not null) throw Fail;
            return Task.FromResult(Snapshots.GetValueOrDefault(number));
        }

        public Task<IReadOnlyList<PullFile>> GetPullFilesAsync(int number, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PullFile>>(Files);
    }

    private static ReviewServerOptions Options(TempRepo repo) => new()
    {
        Directory = repo.Path,
        Analyze = ReviewPipelineTests.Fake([]),
        Build = false,
        Log = Console.Error.WriteLine,
    };

    private static HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer() }) { Timeout = TimeSpan.FromSeconds(20) };

    [Fact]
    public async Task GeneratesAConceptMapAfterASkeletonReviewThenSwitchesToItAndSavesIt()
    {
        using var repo = new TempRepo();
        using var temp = new TempDirectory();
        ReviewPipelineTests.WriteApp(repo, "class Thing { }\n");
        var before = repo.Commit("one");
        ReviewPipelineTests.WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var after = repo.Commit("two");
        // Narratives are asked for too, and get answers that explain nothing.
        var claude = new FakeClaude(r => r.Schema == Prompts.Read("map.schema.json") ? MapGeneratorTests.Complete
            : r.Schema == Prompts.Read("summary.schema.json") ? """{ "text": "", "parts": [] }""" : """{ "edges": [], "nodes": [] }""");
        await using var server = new ReviewServer(Options(repo) with { Cache = new ReviewCache(temp.Path, Path.Combine(temp.Path, "repo")), Claude = claude });
        using var client = Client();
        await client.GetAsync(await server.StartAsync());
        var id = $"{before[..7]}-{after[..7]}";
        var api = $"{server.BaseAddress}/api/reviews/{id}";

        await client.PostAsJsonAsync(server.BaseAddress + "/api/reviews", new { range = $"{before}..{after}" });
        var events = await ReadEventsAsync(client, api + "/events", e => Is(e, "map", "ready") || Is(e, "map", "failed"));
        var mapReady = events[^1];
        Assert.Equal(("ready", 1), (mapReady.GetProperty("state").GetString(), mapReady.GetProperty("version").GetInt32()));
        Assert.Contains(events, e => Is(e, "map", "generating"));
        Assert.Contains("\"mapKind\":\"skeleton\"", await client.GetStringAsync($"{server.BaseAddress}/reviews/{id}/changeset.js"), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(api + "/map", new { action = "save" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(api + "/map", new { action = "switch" })).StatusCode);
        var seq = mapReady.GetProperty("seq").GetInt32();
        await ReadEventsAsync(client, api + "/events", e => e.GetProperty("type").GetString() == "ready" && e.GetProperty("seq").GetInt32() > seq);
        var script = await client.GetStringAsync($"{server.BaseAddress}/reviews/{id}/changeset.js");
        Assert.Contains("\"mapKind\":\"generated\"", script, StringComparison.Ordinal);
        Assert.Contains("\"mapVersion\":1", script, StringComparison.Ordinal);
        Assert.Equal("claude-opus-5-5", Assert.Single(claude.Requests, r => r.Schema == Prompts.Read("map.schema.json")).Model);

        var saved = await client.PostAsJsonAsync(api + "/map", new { action = "save" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Contains("\"shared\"", File.ReadAllText(Path.Combine(repo.Path, "app", "vibecheck.json")), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(api + "/map", new { action = "save" })).StatusCode);
    }

    [Fact]
    public async Task SaysWhyThereIsNoConceptMapWhenClaudeIsMissing()
    {
        using var repo = new TempRepo();
        ReviewPipelineTests.WriteApp(repo, "class Thing { }\n");
        var before = repo.Commit("one");
        ReviewPipelineTests.WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var after = repo.Commit("two");
        await using var server = new ReviewServer(Options(repo) with { ClaudeNotice = "no claude here" });
        using var client = Client();
        await client.GetAsync(await server.StartAsync());

        await client.PostAsJsonAsync(server.BaseAddress + "/api/reviews", new { range = $"{before}..{after}" });
        var events = await ReadEventsAsync(client, $"{server.BaseAddress}/api/reviews/{before[..7]}-{after[..7]}/events", e => Is(e, "map", "unavailable"));
        Assert.Equal("no claude here", events[^1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task StreamsCheckedNarrativesWrittenInAWorktreeOfTheHeadAndRegeneratesOne()
    {
        using var repo = new TempRepo();
        ReviewPipelineTests.WriteApp(repo, "class Thing { }\n");
        repo.Write("app/vibecheck.json", MapWriter.Write(TestData.Map()));
        var before = repo.Commit("one");
        ReviewPipelineTests.WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var after = repo.Commit("call the service");
        var seen = new List<string>();
        var regenerated = false;
        var claude = new FakeClaude(r =>
        {
            seen.Add(File.ReadAllText(Path.Combine(r.WorkingDirectory!, TestData.X1)));
            if (r.Schema.Contains("\"parts\"", StringComparison.Ordinal)) return """{ "text": "Thing starts using the service.", "parts": ["a", "b"] }""";
            var text = regenerated ? "Written again." : "Thing now runs the service.";
            return $$"""{ "edges": [ { "from": "a", "to": "b", "text": "{{text}}", "pairs": [ { "from": "A.X.Thing", "to": "B.Service" } ] } ], "nodes": [] }""";
        });
        string worktree;
        await using (var server = new ReviewServer(Options(repo) with { Directory = Path.Combine(repo.Path, "app"), Claude = claude }))
        {
            using var client = Client();
            await client.GetAsync(await server.StartAsync());
            var id = $"{before[..7]}-{after[..7]}";
            var api = $"{server.BaseAddress}/api/reviews/{id}";

            await client.PostAsJsonAsync(server.BaseAddress + "/api/reviews", new { range = $"{before}..{after}" });
            var events = await ReadEventsAsync(client, api + "/events", e => Is(e, "narratives", "done"));

            var narratives = events.Where(e => e.GetProperty("type").GetString() == "narrative").ToDictionary(e => e.GetProperty("key").GetString()!);
            Assert.Equal(["edge:a→b", "summary"], narratives.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("A.X.Thing → B.Service", narratives["edge:a→b"].GetProperty("cites")[0].GetString());
            Assert.Equal(1, narratives["summary"].GetProperty("run").GetInt32());
            Assert.All(seen, x1 => Assert.Contains("Service.Run", x1, StringComparison.Ordinal));
            Assert.All(claude.Requests, r => Assert.Equal("claude-sonnet-5-5", r.Model));
            worktree = claude.Requests[0].WorkingDirectory!;
            Assert.Contains("\"run\":1", await client.GetStringAsync($"{server.BaseAddress}/reviews/{id}/changeset.js"), StringComparison.Ordinal);

            regenerated = true;
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(api + "/narratives", new { key = "edge:b→a" })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(api + "/narratives", new { key = "edge:a→b" })).StatusCode);
            var again = await ReadEventsAsync(client, api + "/events", e => e.TryGetProperty("text", out var t) && t.GetString() == "Written again.");
            Assert.Equal("edge:a→b", again[^1].GetProperty("key").GetString());
            Assert.True(Directory.Exists(worktree));
        }
        Assert.False(Directory.Exists(worktree));
    }

    private static bool Is(JsonElement e, string type, string state) =>
        e.GetProperty("type").GetString() == type && e.TryGetProperty("state", out var s) && s.GetString() == state;

    /// <summary>Reads a server-sent event stream until a <c>ready</c> or <c>failed</c> event, and returns the events read.</summary>
    private static Task<List<JsonElement>> ReadEventsAsync(HttpClient client, string url) =>
        ReadEventsAsync(client, url, e => e.GetProperty("type").GetString() is "ready" or "failed");

    /// <summary>Reads a server-sent event stream until an event meets a condition, and returns the events read.</summary>
    private static async Task<List<JsonElement>> ReadEventsAsync(HttpClient client, string url, Func<JsonElement, bool> last)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var events = new List<JsonElement>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var e = JsonDocument.Parse(line[6..]).RootElement.Clone();
            events.Add(e);
            if (last(e)) break;
        }
        return events;
    }
}
