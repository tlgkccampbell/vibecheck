using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Archmap.Cli;
using Archmap.Format;
using Archmap.Reviewing;
using Xunit;

namespace Archmap.Tests;

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
    }

    [Fact]
    public async Task RunsAReviewAndServesTheViewerOverItsChangeset()
    {
        using var repo = new TempRepo();
        ReviewPipelineTests.WriteApp(repo, "class Thing { }\n");
        repo.Write("app/archmap.json", MapWriter.Write(TestData.Map()));
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
    }

    private static ReviewServerOptions Options(TempRepo repo) => new()
    {
        Directory = repo.Path,
        Analyze = ReviewPipelineTests.Fake([]),
        Build = false,
        PullRequests = false,
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
        var claude = new FakeClaude(MapGeneratorTests.Complete);
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
        Assert.Equal("claude-opus-5-5", Assert.Single(claude.Requests).Model);

        var saved = await client.PostAsJsonAsync(api + "/map", new { action = "save" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Contains("\"shared\"", File.ReadAllText(Path.Combine(repo.Path, "app", "archmap.json")), StringComparison.Ordinal);
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
