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
    public async Task ReadsAPullRequestsSnapshot()
    {
        using var client = Client(_ => Json(Fixture("pull.json")));
        var pull = (await client.GetPullAsync(13894))!;
        Assert.Equal(("OPEN", true, "DIRTY", 4, false), (pull.State, pull.IsDraft, pull.MergeState, pull.Commits, pull.ViewerDidAuthor));
        Assert.Equal(("8f44336ccb889465115aa85319c275c8dec1c3b7", "55dbb4dc6b7edb10b48e3d7fc5bccd32318d1b55"), (pull.HeadOid, pull.BaseOid));
        Assert.Equal("SUCCESS", pull.Checks);
        Assert.Null(pull.LatestReviewCommit);

        using var missing = Client(_ => Json("""{"data":{"repository":{"pullRequest":null}}}"""));
        Assert.Null(await missing.GetPullAsync(1));
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
