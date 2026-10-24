using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vibecheck.Assist;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.GitHub;
using Vibecheck.Model;
using Vibecheck.Reviewing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vibecheck.Cli;

/// <summary>Configures a <see cref="ReviewServer"/>: the repository it reviews, how it analyzes a commit, and how it finds the map.</summary>
public sealed record ReviewServerOptions
{
    /// <summary>Gets the directory inside the repository that reviews start from.</summary>
    public required string Directory { get; init; }

    /// <summary>Gets the analyzer each review uses. <see cref="Analysis.RoslynAnalyzer.AnalyzeAsync"/> is the real one.</summary>
    public required SolutionAnalyzer Analyze { get; init; }

    /// <summary>Gets the cache of the repository's analyses and changesets, or <see langword="null"/> to cache nothing.</summary>
    public ReviewCache? Cache { get; init; }

    /// <summary>Gets a map file on disk that every review uses in place of the repository's own, or <see langword="null"/>.</summary>
    public string? MapPath { get; init; }

    /// <summary>Gets the directory that the paths of <see cref="MapPath"/> are relative to, or <see langword="null"/> for the map's directory.</summary>
    public string? Root { get; init; }

    /// <summary>Gets the solution a review without a map analyzes, relative to <see cref="Directory"/>, or <see langword="null"/> to find it.</summary>
    public string? Solution { get; init; }

    /// <summary>Gets a value indicating whether each end is built before it is analyzed.</summary>
    public bool Build { get; init; } = true;

    /// <summary>Gets a directory to serve the viewer's files from instead of the copies embedded in the tool, or <see langword="null"/>.</summary>
    public string? ViewerDirectory { get; init; }

    /// <summary>Gets the port to listen on, or 0 for any free port.</summary>
    public int Port { get; init; }

    /// <summary>Gets the client that lists and reads the repository's pull requests, or <see langword="null"/> when GitHub is unavailable or turned off.</summary>
    public IGitHub? GitHub { get; init; }

    /// <summary>Gets what the picker tells the reviewer when <see cref="GitHub"/> is missing, or <see langword="null"/> to say nothing.</summary>
    public string? GitHubNotice { get; init; }

    /// <summary>Gets how often a pull request is read again while a page follows its review.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the Claude client that generates maps and places types, or <see langword="null"/> to draw the skeleton alone.</summary>
    public IClaude? Claude { get; init; }

    /// <summary>Gets the model each kind of request to <see cref="Claude"/> uses.</summary>
    public ClaudeModels Models { get; init; } = ClaudeModels.Default;

    /// <summary>Gets what a review drawn with the skeleton tells the reviewer when <see cref="Claude"/> is missing, or <see langword="null"/> to say nothing.</summary>
    public string? ClaudeNotice { get; init; }

    /// <summary>Gets a sink for the lines the server prints about each review, or <see langword="null"/>.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Serves the review workflow on the loopback interface: the range picker, each review's progress as server-sent events,
/// and the viewer over each finished review's changeset. Every request must carry the session's token, which the URL
/// that <see cref="StartAsync"/> returns sets as a cookie, and must name the loopback address in its <c>Host</c> header.
/// See <c>docs/review-workflow.md</c> sections 3 and 8.
/// </summary>
public sealed class ReviewServer : IAsyncDisposable
{
    private readonly ReviewServerOptions options;
    private readonly string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly ConcurrentDictionary<string, Review> reviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MapJob> mapJobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly ReviewedFiles reviewed;

    // The ends of the logs of finished GitHub Actions jobs, by job identifier.
    private readonly ConcurrentDictionary<long, List<string>> jobLogs = new();
    private WebApplication? app;
    private int port;

    public ReviewServer(ReviewServerOptions options)
    {
        this.options = options;
        reviewed = new ReviewedFiles(options.Cache?.ReviewedPath);
    }

    /// <summary>Gets the address the server listens on, without the token.</summary>
    public string BaseAddress => $"http://127.0.0.1:{port}";

    /// <summary>Gets the name of the cookie that carries the token. It names the port, since browsers share cookies across the ports of one host.</summary>
    private string CookieName => $"vibecheck-{port}";

    /// <summary>Starts listening and returns the URL that opens the picker and sets the token cookie.</summary>
    public async Task<string> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        app = builder.Build();
        // Kestrel waits for open requests before it stops, and an event stream stays open until the server ends it.
        // Ending the streams and the running work as soon as stopping begins keeps Ctrl+C from waiting out the shutdown timeout.
        app.Lifetime.ApplicationStopping.Register(stopping.Cancel);
        app.Use(GuardAsync);
        MapEndpoints(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        port = new Uri(address).Port;
        return Link("/");
    }

    /// <summary>Returns the URL of a path on the server with the token in its query, which sets the token cookie when opened.</summary>
    public string Link(string path) => $"{BaseAddress}{path}?token={token}";

    /// <summary>
    /// Starts a review of a range, or returns the review of the same two commits already started, and returns the URL
    /// path of its page.
    /// </summary>
    public string Begin(string range, string? label = null)
    {
        var (baseCommit, headCommit) = RevisionChangeset.ResolveRange(options.Directory, range);
        if (baseCommit == headCommit)
        {
            throw new ArgumentException(range.Contains(WorkingTree.Name, StringComparison.Ordinal)
                ? "there are no uncommitted changes to review" : $"{range} compares a commit with itself, so there is nothing to review");
        }
        var id = $"{baseCommit[..7]}-{headCommit[..7]}";
        lock (reviews)
        {
            // A review that failed is started again, since a failure such as a package restore without a network need not recur.
            label ??= range == WorkingTree.Name ? WorkingTree.Subject : range;
            if (!reviews.TryGetValue(id, out var review) || review.Failed) reviews[id] = Start(new Review(id, baseCommit, headCommit, label));
        }
        return $"/reviews/{id}/";
    }

    /// <summary>
    /// Starts a review of a pull request, or returns the review of the same pull request and head already started, and
    /// returns the URL path of its page. The review compares the pull request's range as GitHub does, or the commits
    /// from <paramref name="from"/> to its head when that is given. See <c>docs/pull-requests.md</c> section 4.
    /// </summary>
    /// <exception cref="ArgumentException">GitHub is not available, the pull request does not exist, or <paramref name="from"/> is not a commit hash.</exception>
    /// <exception cref="GitHubException">GitHub could not be read.</exception>
    public async Task<string> BeginPullAsync(int number, string? from = null)
    {
        var github = options.GitHub ?? throw new ArgumentException(options.GitHubNotice ?? "GitHub is turned off, so pull requests cannot be opened.");
        if (from is not null && !System.Text.RegularExpressions.Regex.IsMatch(from, "^[0-9a-f]{40}$")) throw new ArgumentException("from must be a full commit hash");
        var pull = await github.GetPullAsync(number, stopping.Token) ?? throw new ArgumentException($"{github.Repository.NameWithOwner} has no pull request #{number}");
        var id = $"pr{number}-" + (from is null ? "" : from[..7] + "-") + pull.HeadOid[..7];
        lock (reviews)
        {
            if (!reviews.TryGetValue(id, out var review) || review.Failed)
            {
                review = new Review(id, null, pull.HeadOid, $"#{number} {pull.Title}" + (from is null ? "" : " since your last review")) { Since = from };
                review.Attach(number, pull);
                reviews[id] = Start(review);
            }
        }
        return $"/reviews/{id}/";
    }

    /// <summary>Stops the host as Ctrl+C does.</summary>
    public void Stop() => app?.Lifetime.StopApplication();

    /// <summary>Waits until the host stops, which Ctrl+C causes.</summary>
    public Task WaitForShutdownAsync() => app?.WaitForShutdownAsync() ?? Task.CompletedTask;

    /// <summary>Stops the server, cancels the reviews still running and waits for them to remove their worktrees.</summary>
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        Task?[] tasks;
        lock (mapJobs) tasks = [.. reviews.Values.Select(r => r.Task), .. reviews.Values.Select(r => r.Narratives?.Task), .. mapJobs.Values.Select(j => j.Task)];
        var running = tasks.Where(t => t is not null).Cast<Task>().ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(15)));
        foreach (var r in reviews.Values) r.Worktree?.Dispose();
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        stopping.Dispose();
    }

    private Review Start(Review review)
    {
        options.Log?.Invoke($"reviewing {review.Label} ({(review.Base is null ? "pull request" : review.Base[..8])}..{review.Head[..8]})");
        review.Add(new { type = "review", label = review.Label, @base = review.Base, head = review.Head });
        if (review.Snapshot is { } snapshot) PublishSnapshot(review, snapshot);
        review.Task = Task.Run(() => RunAsync(review, rerun: false));
        return review;
    }

    /// <summary>
    /// Runs the pipeline for a review and records its result. A first run that fails marks the review failed. A rerun,
    /// which switches a finished review to a newer map, keeps the result it had when it fails.
    /// </summary>
    private async Task RunAsync(Review review, bool rerun)
    {
        var progress = new Progress(p =>
        {
            options.Log?.Invoke($"  {review.Id} {p.Stage}: {p.Message}");
            review.Add(new { type = "stage", stage = p.Stage, message = p.Message });
        });
        try
        {
            if (!rerun) await PreparePullAsync(review, progress);
            var request = new ReviewRequest(options.Directory, review.Base!, review.Head)
            {
                MapPath = options.MapPath, Root = options.Root, Solution = options.Solution, Build = options.Build, Renames = review.Renames,
            };
            var result = await ReviewPipeline.RunAsync(request, options.Analyze, options.Cache, progress, stopping.Token);
            review.Publish(result);
            foreach (var w in result.Info.Warnings) options.Log?.Invoke($"  {review.Id} warning: {w}");
            options.Log?.Invoke($"  {review.Id} ready");
            review.Add(new { type = "ready", url = $"/reviews/{review.Id}/" });
            ImproveMap(review, result);
            Explain(review, regenerate: false);
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "cancelled" : ex.Message;
            options.Log?.Invoke($"  {review.Id} failed: {message}");
            if (rerun)
            {
                review.Add(new { type = "map", state = "failed", message = "switching to the concept map failed: " + message });
            }
            else
            {
                review.Failed = true;
                review.Add(new { type = "failed", message });
            }
        }
        finally
        {
            review.Rerunning = false;
        }
    }

    /// <summary>
    /// Prepares a review's pull request before analysis. A review started from a pull request fetches the commits
    /// that are missing, takes its base as GitHub does, and pairs renames as GitHub does. A review of a range is
    /// attached to the open pull request whose head is its head, when exactly one is. See <c>docs/pull-requests.md</c>
    /// sections 4.1 to 4.5.
    /// </summary>
    private async Task PreparePullAsync(Review review, IProgress<ReviewProgress> progress)
    {
        if (options.GitHub is not { } github) return;
        if (review.Pull is not { } number)
        {
            try
            {
                var matches = (await github.ListPullsAsync(stopping.Token)).Where(p => p.HeadOid == review.Head).ToList();
                if (matches.Count != 1 || await github.GetPullAsync(matches[0].Number, stopping.Token) is not { } found) return;
                review.Attach(matches[0].Number, found);
                options.Log?.Invoke($"  {review.Id} is the head of #{found.Number}");
                PublishSnapshot(review, found);
                EnsureWatching(review);
            }
            catch (GitHubException ex)
            {
                options.Log?.Invoke($"  {review.Id} could not look for its pull request: {ex.Message}");
            }
            return;
        }
        var pull = review.Snapshot!;
        var dir = options.Directory;
        if (PullCommits.Present(dir, pull)) progress.Report(new ReviewProgress("fetch", "the commits are present"));
        else
        {
            var remote = PullCommits.RemoteOf(dir, github.Repository) ?? throw new InvalidOperationException($"no remote of this repository points at {github.Repository.NameWithOwner}");
            progress.Report(new ReviewProgress("fetch", $"fetching #{number} from {remote}"));
            await Task.Run(() => PullCommits.Fetch(dir, remote, number, pull.BaseRefName), stopping.Token);
            if (!PullCommits.Present(dir, pull)) throw new InvalidOperationException($"the commits of #{number} are still missing after fetching it from {remote}");
        }
        var (baseCommit, head) = PullCommits.Range(dir, pull);
        if (review.Since is { } from)
        {
            // Git's own pairing of renames stands for these commits, since GitHub's file list covers the whole pull request.
            if (Git.TryRun(dir, "merge-base", "--is-ancestor", from, head) is null) throw new InvalidOperationException($"{from[..8]}, where your last review was made, is no longer part of #{number}");
            baseCommit = from;
        }
        else
        {
            var files = await github.GetPullFilesAsync(number, stopping.Token);
            review.Renames = files.Where(f => f.PreviousPath is not null).ToDictionary(f => f.Path, f => f.PreviousPath!, StringComparer.Ordinal);
        }
        review.Resolve(baseCommit);
        review.Add(new { type = "review", label = review.Label, @base = baseCommit, head });
        progress.Report(new ReviewProgress("fetch", $"comparing {baseCommit[..8]}..{head[..8]} as GitHub does"));
    }

    /// <summary>Starts following a review's pull request when a page has its event stream open and nothing follows it yet.</summary>
    private void EnsureWatching(Review review)
    {
        lock (review)
        {
            if (review.Pull is null || review.Watchers == 0 || review.Watching) return;
            review.Watching = true;
        }
        _ = Task.Run(() => WatchPullAsync(review));
    }

    /// <summary>
    /// Reads a review's pull request again and again while a page has the review's event stream open: at once, then
    /// at each interval, and whenever a refresh asks for it. GitHub cannot send webhooks to a loopback server, so the
    /// server polls, and the pages never do. See <c>docs/pull-requests.md</c> section 3.3.
    /// </summary>
    private async Task WatchPullAsync(Review review)
    {
        while (true)
        {
            Task poke;
            lock (review)
            {
                if (review.Watchers == 0 || stopping.IsCancellationRequested) { review.Watching = false; return; }
                poke = review.NextPoke();
            }
            var wait = await RefreshPullAsync(review);
            await Task.WhenAny(poke, Task.Delay(wait, stopping.Token).ContinueWith(_ => { }, TaskScheduler.Default));
        }
    }

    /// <summary>
    /// Reads a review's pull request once, sends it to the review's pages when it changed, and returns how long to wait
    /// before the next read. While GitHub limits the rate, nothing is read until the wait it asked for has passed, and
    /// the pages are told that the data is stale.
    /// </summary>
    private async Task<TimeSpan> RefreshPullAsync(Review review)
    {
        if (options.GitHub is not { } github || review.Pull is not { } number) return options.PollInterval;
        var quiet = review.QuietUntil - DateTime.UtcNow;
        if (quiet > TimeSpan.Zero) return quiet;
        try
        {
            if (await github.GetPullAsync(number, stopping.Token) is { } snapshot) PublishSnapshot(review, snapshot);
            return options.PollInterval;
        }
        catch (GitHubRateLimitException ex)
        {
            review.QuietUntil = DateTime.UtcNow + ex.RetryAfter;
            PublishStale(review, ex.Message);
            return ex.RetryAfter > options.PollInterval ? ex.RetryAfter : options.PollInterval;
        }
        catch (GitHubException ex)
        {
            PublishStale(review, ex.Message);
            return options.PollInterval;
        }
        catch (OperationCanceledException)
        {
            return options.PollInterval;
        }
    }

    /// <summary>
    /// Sends a review's pages a <c>pull</c> event with a snapshot, and with where its threads go in the review's diffs,
    /// unless it is the snapshot they were last sent.
    /// </summary>
    private void PublishSnapshot(Review review, PullSnapshot snapshot)
    {
        var text = JsonSerializer.Serialize(snapshot, FactsJson.Options);
        lock (review)
        {
            review.Snapshot = snapshot;
            if (text == review.SentSnapshot && review.Stale is null) return;
            review.SentSnapshot = text;
            review.Stale = null;
        }
        review.Add(new { type = "pull", snapshot, threads = Placements(review, snapshot), annotations = AnnotationPlaces(review, snapshot) });
    }

    /// <summary>
    /// Returns where a pull request's threads go in a review's diffs, by thread identifier, or <see langword="null"/>
    /// before the review has a result. See <c>docs/pull-requests.md</c> section 7.1.
    /// </summary>
    private Dictionary<string, ThreadPlace>? Placements(Review review, PullSnapshot? snapshot)
    {
        if (review.Result is not { } result || snapshot?.Discussion is not { } discussion) return null;
        var sameHead = review.Head == snapshot.HeadOid;
        // A review's base is the pull request's when it is the merge base of the pull request's current base and head.
        var sameBase = sameHead && review.Since is null && review.Base is { } b && Git.TryRun(options.Directory, "merge-base", snapshot.BaseOid, snapshot.HeadOid) == b;
        return ThreadPlacer.Place(discussion.Threads, result.Changeset.Hunks, result.Target.Root, review.Head, sameHead, sameBase);
    }

    /// <summary>
    /// Returns where the annotations of a pull request's checks go in a review's diffs, keyed as
    /// <see cref="ThreadPlacer.PlaceAnnotations"/> keys them, or <see langword="null"/> before the review has a result.
    /// See <c>docs/pull-requests.md</c> section 9.
    /// </summary>
    private static Dictionary<string, ThreadPlace>? AnnotationPlaces(Review review, PullSnapshot? snapshot) =>
        review.Result is { } result && snapshot?.CheckList is { } checks
            ? ThreadPlacer.PlaceAnnotations(checks, result.Changeset.Hunks, result.Target.Root, review.Head == snapshot.HeadOid)
            : null;

    /// <summary>Sends a review's pages a <c>pull</c> event saying that the snapshot they have is stale, and why, unless they were told already.</summary>
    private static void PublishStale(Review review, string message)
    {
        lock (review)
        {
            if (review.Stale == message) return;
            review.Stale = message;
        }
        review.Add(new { type = "pull", stale = true, message });
    }

    /// <summary>
    /// Starts the map work a finished review calls for: generating a concept map when it was drawn with the skeleton,
    /// and placing the unsorted types when it was drawn with a generated map that leaves some. Reviews of the same
    /// solution share one job, and each is told when the job finishes. See <c>docs/review-workflow.md</c> sections 4.4
    /// and 4.5.
    /// </summary>
    private void ImproveMap(Review review, ReviewResult result)
    {
        var kind = result.Info.MapKind;
        var unsorted = result.Changeset.Types.Any(t => t.LeafHead == ArchitectureMap.Unsorted || t.LeafBase == ArchitectureMap.Unsorted);
        if (kind != "skeleton" && !(kind == "generated" && unsorted)) return;
        if (options.Claude is null || options.Cache is null)
        {
            if (kind == "skeleton" && options.ClaudeNotice is not null) review.Add(new { type = "map", state = "unavailable", message = options.ClaudeNotice });
            return;
        }
        var key = result.Target.Root + "|" + result.Target.Solution + "|" + kind + "|" + result.Info.MapVersion;
        MapJob job;
        lock (mapJobs)
        {
            if (!mapJobs.TryGetValue(key, out job!))
            {
                job = new MapJob(kind == "skeleton" ? "generating" : "placing");
                mapJobs[key] = job;
                job.Task = Task.Run(() => RunMapJobAsync(key, job, result));
            }
            job.Subscribe(review);
        }
    }

    private async Task RunMapJobAsync(string key, MapJob job, ReviewResult result)
    {
        var analysis = ReviewPipeline.Union(result.HeadAnalysis, result.BaseAnalysis);
        var progress = new ImmediateProgress(m => { options.Log?.Invoke($"  map: {m}"); job.Report(new { type = "map", state = job.State, message = m }); });
        try
        {
            string text, message;
            if (job.State == "generating")
            {
                var generated = await MapGenerator.GenerateAsync(result.Changeset.Map, analysis, options.Claude!, progress, stopping.Token, options.Models.Map);
                text = generated.Text;
                message = generated.Findings.Count == 0 ? "A concept map is ready."
                    : $"A concept map is ready. The checker still reports {generated.Findings.Count} finding(s) about it.";
            }
            else
            {
                var placement = await TypePlacer.PlaceAsync(result.Changeset.Map, analysis, options.Claude!, progress, stopping.Token, options.Models.Placement);
                if (placement is null || placement.Placed == 0) { job.Report(new { type = "map", state = "done" }); return; }
                text = Format.MapWriter.Write(placement.Map);
                message = $"An updated concept map places {placement.Placed} new type(s).";
            }
            var version = options.Cache!.WriteGeneratedMap(result.Target, text);
            options.Log?.Invoke($"  map: version {version} written");
            job.Report(new { type = "map", state = "ready", version, message });
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "cancelled" : ex.Message;
            options.Log?.Invoke($"  map: failed: {message}");
            // A failed job is forgotten, so that the next review of the solution tries again.
            lock (mapJobs) mapJobs.Remove(key);
            job.Report(new { type = "map", state = "failed", message = (job.State == "generating" ? "Claude could not generate a concept map: " : "Claude could not place the new types: ") + message });
        }
    }

    /// <summary>
    /// Starts writing the narratives of a review's current result, in the background, and cancels any narratives still
    /// being written for an earlier result. Each narrative that passes its check is sent as a <c>narrative</c> event,
    /// and the job's progress as <c>narratives</c> events, all carrying the run of the result they explain. Claude reads
    /// the code in a worktree of the head commit, which is kept for the life of the review. See
    /// <c>docs/review-workflow.md</c> section 6.
    /// </summary>
    private void Explain(Review review, bool regenerate)
    {
        if (options.Claude is null) return;
        var (result, run) = review.Current;
        if (result is null) return;
        var job = new NarrativeJob(run, CancellationTokenSource.CreateLinkedTokenSource(stopping.Token));
        lock (review)
        {
            review.Narratives?.Cancel();
            review.Narratives = job;
        }
        job.Task = Task.Run(async () =>
        {
            var ct = job.Cancellation.Token;
            try
            {
                var top = Git.Run(options.Directory, "rev-parse", "--show-toplevel");
                lock (review) review.Worktree ??= Reviewing.Worktree.Add(top, review.Head);
                // A review has its base by the time it has a result, since a pull request's review resolves it first.
                job.Context = new NarrativeContext(result.Changeset, result.BaseAnalysis, result.HeadAnalysis, NarrativeContext.ReadCommits(top, review.Base!, review.Head))
                {
                    WorkingDirectory = Path.GetFullPath(Path.Combine(review.Worktree.Path, result.Target.Root)),
                    Model = options.Models.Narrative,
                };
                job.Plan = NarrativePlanner.Plan(result.Changeset);
                var total = job.Plan.Sum(b => b.Keys.Count());
                options.Log?.Invoke($"  {review.Id} narratives: {total} in {job.Plan.Count} request(s)");
                review.Add(new { type = "narratives", run, state = "writing", total, message = $"Claude is explaining the changes: {total} narratives." });
                var tally = await NarrativeWriter.RunAsync(job.Plan, job.Context, options.Claude, options.Cache,
                    n => review.Add(new { type = "narrative", run, key = n.Key, text = n.Text, cites = n.Cites }), regenerate, ct);
                foreach (var f in tally.Failures) options.Log?.Invoke($"  {review.Id} narratives failed: {f}");
                options.Log?.Invoke($"  {review.Id} narratives: {tally.Written} written, {tally.Dropped} dropped");
                review.Add(new { type = "narratives", run, state = "done", written = tally.Written, dropped = tally.Dropped, failures = tally.Failures });
            }
            catch (OperationCanceledException)
            {
                // A newer result, a regeneration or the server's shutdown took over.
            }
            catch (Exception ex)
            {
                options.Log?.Invoke($"  {review.Id} narratives failed: {ex.Message}");
                review.Add(new { type = "narratives", run, state = "failed", message = "Claude could not explain the changes: " + ex.Message });
            }
        });
    }

    /// <summary>
    /// Writes one narrative of a review anew, in the background, and sends it as a <c>narrative</c> event, or sends a
    /// <c>narrative</c> event with <c>failed</c> set when it cannot be written. Returns a value indicating whether the
    /// key names a narrative of the review's current plan.
    /// </summary>
    private bool Regenerate(Review review, string key)
    {
        var job = review.Narratives;
        var batch = job?.Plan?.FirstOrDefault(b => b.Keys.Contains(key));
        if (job is null || batch is null || job.Context is null || options.Claude is null) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                var n = await NarrativeWriter.RegenerateAsync(batch, key, job.Context, options.Claude, options.Cache, job.Cancellation.Token);
                if (n is not null) review.Add(new { type = "narrative", run = job.Run, key = n.Key, text = n.Text, cites = n.Cites });
                else review.Add(new { type = "narrative", run = job.Run, key, failed = true, message = "The new narrative failed its check, so the old one stays." });
            }
            catch (Exception ex) when (ex is ClaudeException or OperationCanceledException)
            {
                review.Add(new { type = "narrative", run = job.Run, key, failed = true, message = ex is ClaudeException ? "Claude could not write it: " + ex.Message : "cancelled" });
            }
        });
        return true;
    }

    /// <summary>
    /// Writes a review's generated map into the repository as <c>vibecheck.json</c> beside the solution, where reviews of
    /// the uncommitted work find it at once and reviews of later commits find it once it is committed. An existing file
    /// is never overwritten.
    /// </summary>
    private IResult SaveMap(Review review)
    {
        var result = review.Result;
        if (result is null || result.Info.MapKind != "generated") return Results.BadRequest(new { message = "Only a generated map can be saved." });
        var top = Git.Run(options.Directory, "rev-parse", "--show-toplevel");
        var relative = result.Target.Root == "." ? MapLocator.FileName : result.Target.Root + "/" + MapLocator.FileName;
        var path = Path.Combine(top, relative);
        if (File.Exists(path)) return Results.Conflict(new { message = $"{relative} already exists. Delete it, or compare it with the generated map, before saving." });
        File.WriteAllText(path, Format.MapWriter.Write(result.Changeset.Map));
        options.Log?.Invoke($"  {review.Id} saved the generated map to {relative}");
        return Results.Json(new { message = $"Saved {relative}. Commit it to make it the repository's map." }, FactsJson.Options);
    }

    /// <summary>
    /// Rejects a request whose <c>Host</c> header does not name the loopback address and port, which stops DNS
    /// rebinding, and a request without the token, which stops other pages in the browser. A request that carries the
    /// token in its query sets the cookie and is sent on without it. A request that writes, such as a POST or a DELETE,
    /// must send JSON, which a cross-site form cannot.
    /// </summary>
    private async Task GuardAsync(HttpContext ctx, RequestDelegate next)
    {
        var host = ctx.Request.Host;
        if (host.Host is not ("127.0.0.1" or "localhost") || host.Port != port)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (ctx.Request.Query.TryGetValue("token", out var given) && Matches(given.ToString()))
        {
            ctx.Response.Cookies.Append(CookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
            var rest = ctx.Request.Query.Where(q => q.Key != "token").Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value.ToString())}");
            var query = string.Join('&', rest);
            ctx.Response.Redirect(ctx.Request.Path + (query.Length > 0 ? "?" + query : ""));
            return;
        }
        if (!Matches(ctx.Request.Cookies[CookieName]))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Open the link that the vibecheck command printed.");
            return;
        }
        var method = ctx.Request.Method;
        var writes = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
        if (writes && ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The server has no other log, so a failure in a handler is printed where the reviewer started the tool.
            options.Log?.Invoke($"error serving {ctx.Request.Path}: {ex}");
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsync(ex.Message);
        }
    }

    private bool Matches(string? value) =>
        value is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value), Encoding.ASCII.GetBytes(token));

    private void MapEndpoints(WebApplication web)
    {
        web.MapGet("/", (HttpContext ctx) => HtmlPage(ctx, ReadViewer("start.html")));

        web.MapGet("/api/ranges", async () =>
        {
            var ranges = RangeCatalog.List(options.Directory);
            var (pulls, notice) = await ListPullsAsync();
            var top = Git.Run(options.Directory, "rev-parse", "--show-toplevel");
            var open = reviews.Values.OrderBy(r => r.Started).Select(r => new { r.Id, r.Label, state = r.State, url = $"/reviews/{r.Id}/" });
            return Results.Json(new { repository = Path.GetFileName(top), ranges, pulls, github = notice, reviews = open }, FactsJson.Options);
        });

        web.MapPost("/api/reviews", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<BeginBody>(ctx.Request.Body, FactsJson.Options);
            if (body?.Pull is null && string.IsNullOrWhiteSpace(body?.Range)) return Results.BadRequest(new { message = "a range or a pull request is required" });
            try
            {
                var url = body.Pull is { } pull ? await BeginPullAsync(pull, body.From) : Begin(body.Range!.Trim(), body.Label);
                return Results.Json(new { id = url.Split('/')[2], url }, FactsJson.Options);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (GitHubException ex)
            {
                return Results.Json(new { message = ex.Message }, FactsJson.Options, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        web.MapGet("/api/reviews/{id}/events", async (string id, HttpContext ctx) =>
        {
            if (!reviews.TryGetValue(id, out var review)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var ct = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping.Token).Token;
            var sent = 0;
            lock (review) review.Watchers++;
            EnsureWatching(review);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var (events, next) = review.From(sent);
                    foreach (var e in events) await ctx.Response.WriteAsync($"data: {e}\n\n", ct);
                    sent += events.Count;
                    // A comment line now and then keeps proxies and the browser from closing an idle stream.
                    if (events.Count == 0) await ctx.Response.WriteAsync(": keep-alive\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(15), ct));
                }
            }
            catch (OperationCanceledException)
            {
                // The browser closed the stream, or the server is stopping.
            }
            finally
            {
                lock (review) review.Watchers--;
            }
        });

        web.MapPost("/api/reviews/{id}/map", async (string id, HttpContext ctx) =>
        {
            if (!reviews.TryGetValue(id, out var review) || review.Result is null) return Results.NotFound();
            var body = await JsonSerializer.DeserializeAsync<MapBody>(ctx.Request.Body, FactsJson.Options);
            switch (body?.Action)
            {
                case "switch":
                    lock (review)
                    {
                        if (review.Rerunning) return Results.Accepted();
                        review.Rerunning = true;
                    }
                    review.Task = Task.Run(() => RunAsync(review, rerun: true));
                    return Results.Accepted();
                case "save":
                    return SaveMap(review);
                default:
                    return Results.BadRequest(new { message = "the action must be switch or save" });
            }
        });

        web.MapPost("/api/reviews/{id}/narratives", async (string id, HttpContext ctx) =>
        {
            if (!reviews.TryGetValue(id, out var review) || review.Result is null) return Results.NotFound();
            if (options.Claude is null) return Results.BadRequest(new { message = "Claude is not available, so there are no narratives." });
            var body = await JsonSerializer.DeserializeAsync<NarrativesBody>(ctx.Request.Body, FactsJson.Options);
            if (body?.Key is null)
            {
                Explain(review, regenerate: true);
                return Results.Accepted();
            }
            return Regenerate(review, body.Key) ? Results.Accepted() : Results.NotFound(new { message = $"{body.Key} is not among the review's narratives." });
        });

        web.MapPost("/api/reviews/{id}/reviewed", async (string id, HttpContext ctx) =>
        {
            if (!reviews.TryGetValue(id, out var review) || review.Result is not { } result) return Results.NotFound();
            var body = await JsonSerializer.DeserializeAsync<ReviewedBody>(ctx.Request.Body, FactsJson.Options);
            if (body?.Paths is not { Count: > 0 } || body.Reviewed is not { } mark) return Results.BadRequest(new { message = "paths and reviewed are required" });
            var keys = reviewed.Set(result.Changeset, result.Target.Root, body.Paths, mark);
            options.Log?.Invoke($"  {review.Id} {(mark ? "marked" : "unmarked")} {keys.Count} file(s) reviewed");
            // Every review that shows one of the same changes is told, so that the pages open on any of them agree.
            foreach (var r in reviews.Values) AnnounceMarks(r, keys);
            return Results.Json(new { marks = reviewed.Marks(result.Changeset, result.Target.Root) }, FactsJson.Options);
        });

        web.MapGet("/api/reviews/{id}/pull", (string id) =>
            reviews.TryGetValue(id, out var r) && r.Snapshot is { } snapshot ? Results.Json(snapshot, FactsJson.Options) : Results.NotFound());

        web.MapPost("/api/reviews/{id}/pull/refresh", async (string id) =>
        {
            if (!reviews.TryGetValue(id, out var review) || review.Pull is null) return Results.NotFound();
            await RefreshPullAsync(review);
            return Results.Json(new { snapshot = review.Snapshot, stale = review.Stale }, FactsJson.Options);
        });

        web.MapPost("/api/reviews/{id}/pull/comments", async (string id, HttpContext ctx) =>
        {
            if (Writer(id, out var refusal) is not var (review, github, pull)) return refusal;
            var body = await JsonSerializer.DeserializeAsync<CommentBody>(ctx.Request.Body, FactsJson.Options);
            if (string.IsNullOrWhiteSpace(body?.Body)) return Results.BadRequest(new { message = "a comment needs a body" });
            var text = body.Body;
            if (body.Path is null) return await WriteAsync(review, $"commented on the conversation: {Excerpt(text)}", () => github.AddCommentAsync(pull.NodeId!, text, stopping.Token));
            if (review.Result is not { } result) return Results.Conflict(new { message = "the review is not ready" });
            var file = result.Changeset.Hunks.FirstOrDefault(h => h.Path == body.Path);
            if (file is null || body.Hunk is not { } hunk || body.Last is not { } last || body.Side is not ("LEFT" or "RIGHT"))
                return Results.BadRequest(new { message = "a comment on lines needs a path that the diff holds, a hunk, its last line and a side" });
            DiffAnchor at;
            try
            {
                at = ThreadPlacer.Anchor(ReviewPipeline.RepositoryPath(body.Path, result.Target.Root), file.Hunks, hunk, body.First ?? last, last, body.Side == "RIGHT");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            var where = $"{at.Path}:{(at.StartLine is { } start ? start + "-" : "")}{at.Line} {at.Side}";
            if (body.Review != true) return await WriteAsync(review, $"commented on {where}: {Excerpt(text)}", () => github.AddSingleCommentAsync(pull.Number, review.Head, at, text, stopping.Token));
            return await WriteAsync(review, $"added a review comment on {where}: {Excerpt(text)}", async () =>
            {
                // The pending review is created when the first comment needs one, on the review's head.
                var pending = pull.Discussion?.Pending?.Id ?? await github.StartReviewAsync(pull.NodeId!, review.Head, stopping.Token);
                await github.AddReviewThreadAsync(pending, at, text, stopping.Token);
            });
        });

        web.MapPost("/api/reviews/{id}/pull/threads/{thread}/replies", async (string id, string thread, HttpContext ctx) =>
        {
            if (Writer(id, out var refusal) is not var (review, github, pull)) return refusal;
            if (pull.Discussion?.Threads.FirstOrDefault(t => t.Id == thread) is not { } found) return Results.NotFound(new { message = $"#{pull.Number} has no thread {thread}" });
            var body = await JsonSerializer.DeserializeAsync<ReplyBody>(ctx.Request.Body, FactsJson.Options);
            if (string.IsNullOrWhiteSpace(body?.Body)) return Results.BadRequest(new { message = "a reply needs a body" });
            // A reply joins the pending review when there is one and posts at once otherwise, as on GitHub.
            var pending = pull.Discussion.Pending?.Id;
            return await WriteAsync(review, $"replied{(pending is null ? "" : " in the pending review")} on {found.Path}: {Excerpt(body.Body)}", () => github.ReplyAsync(thread, body.Body, pending, stopping.Token));
        });

        web.MapPost("/api/reviews/{id}/pull/threads/{thread}/resolved", async (string id, string thread, HttpContext ctx) =>
        {
            if (Writer(id, out var refusal) is not var (review, github, pull)) return refusal;
            if (pull.Discussion?.Threads.FirstOrDefault(t => t.Id == thread) is not { } found) return Results.NotFound(new { message = $"#{pull.Number} has no thread {thread}" });
            var body = await JsonSerializer.DeserializeAsync<ResolvedBody>(ctx.Request.Body, FactsJson.Options);
            if (body?.Resolved is not { } resolved) return Results.BadRequest(new { message = "resolved is required" });
            return await WriteAsync(review, $"{(resolved ? "resolved" : "unresolved")} a thread on {found.Path}", () => github.ResolveAsync(thread, resolved, stopping.Token));
        });

        web.MapPost("/api/reviews/{id}/pull/review", async (string id, HttpContext ctx) =>
        {
            if (Writer(id, out var refusal) is not var (review, github, pull)) return refusal;
            var body = await JsonSerializer.DeserializeAsync<SubmitBody>(ctx.Request.Body, FactsJson.Options);
            if (body?.Event is not ("COMMENT" or "APPROVE" or "REQUEST_CHANGES") || body.Event is not { } kind)
                return Results.BadRequest(new { message = "event must be COMMENT, APPROVE or REQUEST_CHANGES" });
            var summary = body.Body ?? "";
            // GitHub refuses these, so they are refused here before anything is sent. See docs/pull-requests.md section 8.3.
            if (kind != "COMMENT" && pull.ViewerDidAuthor)
                return Results.BadRequest(new { message = "GitHub does not let the author of a pull request approve it or request changes to it" });
            var pending = pull.Discussion?.Pending;
            if (kind != "APPROVE" && string.IsNullOrWhiteSpace(summary) && (pending?.Comments ?? 0) == 0)
                return Results.BadRequest(new { message = "a comment or a request for changes needs a summary or comments" });
            var what = kind switch { "APPROVE" => "approved", "REQUEST_CHANGES" => "requested changes", _ => "submitted a review" };
            return await WriteAsync(review, $"{what} with {pending?.Comments ?? 0} comment(s): {Excerpt(summary)}",
                () => github.SubmitReviewAsync(pull.NodeId!, pending?.Id, review.Head, kind, summary, stopping.Token));
        });

        web.MapDelete("/api/reviews/{id}/pull/review", async (string id) =>
        {
            if (Writer(id, out var refusal) is not var (review, github, pull)) return refusal;
            if (pull.Discussion?.Pending is not { } pending) return Results.NotFound(new { message = $"you have no pending review on #{pull.Number}" });
            return await WriteAsync(review, $"discarded the pending review with {pending.Comments} comment(s)", () => github.DeleteReviewAsync(pending.Id, stopping.Token));
        });

        web.MapGet("/api/reviews/{id}/pull/checks/{job:long}/log", async (string id, long job) =>
        {
            if (!reviews.TryGetValue(id, out var review) || options.GitHub is not { } github || review.Snapshot?.CheckList?.FirstOrDefault(c => c.JobId == job) is not { } check)
                return Results.NotFound(new { message = "the review's pull request has no such job" });
            if (jobLogs.TryGetValue(job, out var cached)) return Results.Json(new { lines = cached }, FactsJson.Options);
            try
            {
                var lines = JobLogs.Tail(await github.GetJobLogAsync(job, stopping.Token), 60);
                // A finished job's log no longer changes, so it is read from GitHub once.
                if (check.State != "pending") jobLogs[job] = lines;
                return Results.Json(new { lines }, FactsJson.Options);
            }
            catch (GitHubException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        web.MapPost("/api/markdown", async (HttpContext ctx) =>
        {
            if (options.GitHub is not { } github) return Results.NotFound(new { message = options.GitHubNotice ?? "GitHub is turned off" });
            var body = await JsonSerializer.DeserializeAsync<MarkdownBody>(ctx.Request.Body, FactsJson.Options);
            try
            {
                return Results.Json(new { html = await github.RenderMarkdownAsync(body?.Text ?? "", stopping.Token) }, FactsJson.Options);
            }
            catch (GitHubException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        web.MapGet("/api/reviews/{id}/changeset", (string id) =>
            reviews.TryGetValue(id, out var r) && r.Result is not null ? Results.Text(FactsJson.Serialize(r.Result.Changeset), "application/json") : Results.NotFound());

        web.MapGet("/reviews/{id}/", (string id, HttpContext ctx) =>
        {
            // Routing matches the path with or without its final slash, but the viewer's scripts are relative to it.
            if (ctx.Request.Path.Value?.EndsWith('/') != true) return Results.Redirect($"/reviews/{id}/");
            if (!reviews.TryGetValue(id, out var r)) return Results.Redirect("/");
            if (r.Result is null) return Results.Redirect($"/?review={Uri.EscapeDataString(id)}");
            // The viewer's page is a body fragment that the artifact host wraps, so it is wrapped here the same way.
            var page = "<!doctype html>\n<html lang=\"en\">\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" + ReadViewer("index.html");
            return HtmlPage(ctx, page);
        });
        web.MapGet("/reviews/{id}/app.js", (string id) => Page("app.js", "text/javascript; charset=utf-8"));
        web.MapGet("/reviews/{id}/changeset.js", (string id) =>
        {
            if (!reviews.TryGetValue(id, out var r)) return Results.NotFound();
            var (result, run) = r.Current;
            if (result is null) return Results.NotFound();
            var info = result.Info;
            var meta = new
            {
                solution = info.Solution, title = info.Title ?? r.Label, range = info.Range, map = info.MapSource, mapKind = info.MapKind,
                mapVersion = info.MapVersion, warnings = info.Warnings, home = "/", review = r.Id, events = r.Count, run,
                narratives = options.Claude is not null, reviewed = reviewed.Marks(result.Changeset, result.Target.Root),
                pull = r.Snapshot, pullThreads = Placements(r, r.Snapshot), pullAnnotations = AnnotationPlaces(r, r.Snapshot), pullCommits = r.OpenedCommits, pullStale = r.Stale,
            };
            var script = $"window.CHANGESET_META = {JsonSerializer.Serialize(meta, FactsJson.Options)};\nwindow.CHANGESET = {FactsJson.Serialize(result.Changeset)}";
            return Results.Text(script, "text/javascript; charset=utf-8");
        });
    }

    /// <summary>
    /// Sends a review a <c>reviewed</c> event with the current marks of its files whose changes have one of the given
    /// keys. Each mark is the reason the file is reviewed, or empty when it is not. A review that shows none of the
    /// changes is sent nothing.
    /// </summary>
    private void AnnounceMarks(Review review, HashSet<string> keys)
    {
        if (review.Result is not { } result) return;
        var paths = ReviewedFiles.Keys(result.Changeset, result.Target.Root).Where(k => keys.Contains(k.Value)).Select(k => k.Key).ToList();
        if (paths.Count == 0) return;
        var marks = reviewed.Marks(result.Changeset, result.Target.Root);
        review.Add(new { type = "reviewed", marks = paths.ToDictionary(p => p, p => marks.GetValueOrDefault(p) ?? "", StringComparer.Ordinal) });
    }

    /// <summary>
    /// Returns the open pull requests for the picker, those that request the reviewer's review first, each saying
    /// whether its head commit is present locally, or none and the reason when GitHub cannot be read.
    /// </summary>
    private async Task<(object[] Pulls, string? Notice)> ListPullsAsync()
    {
        if (options.GitHub is not { } github) return ([], options.GitHubNotice);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            var list = await github.ListPullsAsync(limit.Token);
            // One git process answers for every head: it prints those that exist and passes over the rest.
            var present = (Git.TryRun(options.Directory, ["rev-list", "--no-walk", "--ignore-missing", .. list.Select(p => p.HeadOid)]) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            var pulls = list.OrderByDescending(p => p.ReviewRequested)
                .Select(p => (object)new { p.Number, p.Title, p.Url, p.Author, p.IsDraft, p.ReviewDecision, p.Checks, p.ReviewRequested, local = present.Contains(p.HeadOid) })
                .ToArray();
            return (pulls, null);
        }
        catch (GitHubException ex)
        {
            return ([], ex.Message);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            return ([], "GitHub took too long to list the pull requests.");
        }
    }

    /// <summary>
    /// Returns the review, the client and the snapshot that a write to a review's pull request needs, or null with the
    /// answer that refuses the write: 404 for a review without a pull request, and 409 for a pull request that is closed
    /// or merged, which is shown read-only. See <c>docs/pull-requests.md</c> section 8.
    /// </summary>
    private (Review Review, IGitHub GitHub, PullSnapshot Pull)? Writer(string id, out IResult refusal)
    {
        refusal = Results.NotFound();
        if (!reviews.TryGetValue(id, out var review) || review.Pull is null || options.GitHub is not { } github || review.Snapshot is not { } pull) return null;
        if (pull.State != "OPEN")
        {
            refusal = Results.Conflict(new { message = $"#{pull.Number} is {pull.State.ToLowerInvariant()}, so it is read-only" });
            return null;
        }
        if (pull.NodeId is null)
        {
            refusal = Results.Conflict(new { message = $"GitHub has not identified #{pull.Number} yet" });
            return null;
        }
        return (review, github, pull);
    }

    /// <summary>
    /// Runs a write to a review's pull request and logs what was written, where, and GitHub's answer, since the terminal
    /// is the record of what the tool did as the reviewer. The pull request is then read again at once for every review
    /// of it, so that the pages show the write. A refusal from GitHub is answered with 502 and GitHub's message. See
    /// <c>docs/pull-requests.md</c> section 8.4.
    /// </summary>
    private async Task<IResult> WriteAsync(Review review, string what, Func<Task> write)
    {
        var number = review.Pull!.Value;
        try
        {
            await write();
            options.Log?.Invoke($"  {review.Id} #{number}: {what}: done");
            return Results.Json(new { ok = true }, FactsJson.Options);
        }
        catch (GitHubException ex)
        {
            options.Log?.Invoke($"  {review.Id} #{number}: {what}: GitHub refused: {ex.Message}");
            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
        finally
        {
            await RereadAsync(number);
        }
    }

    /// <summary>Reads a pull request again and sends the snapshot to every review of it whose pages have not seen it.</summary>
    private async Task RereadAsync(int number)
    {
        if (options.GitHub is not { } github) return;
        try
        {
            if (await github.GetPullAsync(number, stopping.Token) is not { } snapshot) return;
            foreach (var r in reviews.Values.Where(r => r.Pull == number)) PublishSnapshot(r, snapshot);
        }
        catch (GitHubException ex)
        {
            options.Log?.Invoke($"  #{number} could not be read again after a write: {ex.Message}");
        }
    }

    /// <summary>Returns the start of a text that the log quotes, on one line.</summary>
    private static string Excerpt(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return "\"" + (line.Length > 60 ? line[..57] + "..." : line) + "\"";
    }

    private IResult Page(string name, string contentType) => Results.Text(ReadViewer(name), contentType);

    /// <summary>
    /// Returns one of the viewer's pages with a <c>Content-Security-Policy</c> that lets it run only the server's
    /// scripts, the pinned libraries it loads, and its own inline scripts, which carry a nonce made for this response.
    /// The viewer shows comment bodies that other people wrote, so the policy keeps a gap in their sanitizing from
    /// running script. See <c>docs/pull-requests.md</c> section 11.
    /// </summary>
    private static IResult HtmlPage(HttpContext ctx, string page)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        ctx.Response.Headers.ContentSecurityPolicy = string.Join("; ",
            "default-src 'none'",
            $"script-src 'self' 'nonce-{nonce}' https://cdn.jsdelivr.net/npm/elkjs@0.9.3/ https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.10.0/",
            // The pages set style attributes from script, and the fonts come from Google Fonts.
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
            "font-src https://fonts.gstatic.com",
            // Comment bodies show images that GitHub and its users host.
            "img-src 'self' https: data:",
            "connect-src 'self'",
            "base-uri 'none'",
            "form-action 'none'",
            "frame-ancestors 'none'");
        return Results.Text(page.Replace("<script>", $"<script nonce=\"{nonce}\">", StringComparison.Ordinal), "text/html; charset=utf-8");
    }

    /// <summary>Returns one of the viewer's files, from the viewer directory when the options name one and from the tool's embedded copy otherwise.</summary>
    private string ReadViewer(string name)
    {
        if (options.ViewerDirectory is not null) return File.ReadAllText(Path.Combine(options.ViewerDirectory, name));
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("viewer/" + name) ?? throw new FileNotFoundException("the viewer file is not embedded", name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record BeginBody(string? Range, string? Label, int? Pull, string? From);

    private sealed record MapBody(string? Action);

    private sealed record NarrativesBody(string? Key);

    private sealed record ReviewedBody(List<string>? Paths, bool? Reviewed);

    /// <summary>
    /// Holds a new comment. Without a path it goes to the conversation. With a path it is on lines of one side of a hunk
    /// of that file, given by their indexes in the hunk, and the review flag adds it to the pending review rather than
    /// posting it at once.
    /// </summary>
    private sealed record CommentBody(string? Body, string? Path, int? Hunk, int? First, int? Last, string? Side, bool? Review);

    private sealed record ReplyBody(string? Body);

    private sealed record ResolvedBody(bool? Resolved);

    private sealed record SubmitBody(string? Event, string? Body);

    private sealed record MarkdownBody(string? Text);

    /// <summary>
    /// Represents the writing of one result's narratives: the run of the result, the plan and its inputs once they are
    /// ready, and the cancellation that a newer job uses to stop it.
    /// </summary>
    private sealed class NarrativeJob(int run, CancellationTokenSource cancellation)
    {
        public int Run { get; } = run;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
        public List<NarrativeBatch>? Plan { get; set; }
        public NarrativeContext? Context { get; set; }

        /// <summary>Cancels the job. Its token source is left for its task to observe, and is collected with it.</summary>
        public void Cancel() => Cancellation.Cancel();
    }

    /// <summary>Reports progress on the calling thread as it arrives.</summary>
    private sealed class ImmediateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>
    /// Represents one map generation or placement for a solution, and the reviews waiting on it. A review that
    /// subscribes late is sent the job's latest event, so that it shows the job's state at once.
    /// </summary>
    private sealed class MapJob(string state)
    {
        private readonly List<Review> subscribers = [];
        private object? latest;

        /// <summary>Gets the job's kind as the viewer names its state: <c>generating</c> or <c>placing</c>.</summary>
        public string State { get; } = state;

        public Task? Task { get; set; }

        public void Subscribe(Review review)
        {
            lock (subscribers)
            {
                if (subscribers.Contains(review)) return;
                subscribers.Add(review);
                review.Add(latest ?? new { type = "map", state = State, message = State == "generating" ? "Claude is generating a concept map." : "Claude is placing the new types." });
            }
        }

        public void Report(object e)
        {
            lock (subscribers)
            {
                latest = e;
                foreach (var r in subscribers) r.Add(e);
            }
        }
    }

    /// <summary>Reports progress on the calling thread as it arrives.</summary>
    private sealed class Progress(Action<ReviewProgress> report) : IProgress<ReviewProgress>
    {
        public void Report(ReviewProgress value) => report(value);
    }

    /// <summary>
    /// Represents one review in this session: its two commits, the events it has produced so far, and its result once
    /// it has one. Every event is kept, so that a page opened late replays the review from its start.
    /// </summary>
    private sealed class Review(string id, string? baseCommit, string headCommit, string label)
    {
        private readonly List<string> events = [];
        private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource poke = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        /// <summary>Gets the base commit, which a pull request's review learns only once its commits are present.</summary>
        public string? Base { get; private set; } = baseCommit;
        public string Head { get; } = headCommit;
        public string Label { get; } = label;
        public DateTime Started { get; } = DateTime.UtcNow;
        public Task? Task { get; set; }
        public ReviewResult? Result { get; private set; }
        public bool Failed { get; set; }
        public bool Rerunning { get; set; }

        /// <summary>Gets the number of results the review has had. Narrative events carry it, so that a page ignores those of an earlier result.</summary>
        public int Run { get; private set; }

        /// <summary>Gets or sets the narratives being written for the current result, or <see langword="null"/>.</summary>
        public NarrativeJob? Narratives { get; set; }

        /// <summary>Gets or sets the worktree of the head commit that narratives read, or <see langword="null"/> before the first needs it.</summary>
        public Worktree? Worktree { get; set; }

        /// <summary>Gets the number of the pull request the review belongs to, or <see langword="null"/>.</summary>
        public int? Pull { get; private set; }

        /// <summary>Gets the commit a review of the commits since the reviewer's last review starts from, or <see langword="null"/>.</summary>
        public string? Since { get; init; }

        /// <summary>Gets or sets the latest snapshot of the review's pull request.</summary>
        public PullSnapshot? Snapshot { get; set; }

        /// <summary>Gets the number of commits the pull request held when the review began, which the new-push notice counts from.</summary>
        public int? OpenedCommits { get; private set; }

        /// <summary>Gets or sets the renames GitHub reports for the pull request, by head path, relative to the repository's top.</summary>
        public IReadOnlyDictionary<string, string>? Renames { get; set; }

        /// <summary>Gets or sets the snapshot last sent to the pages, as JSON, so that an unchanged one is not sent again.</summary>
        public string? SentSnapshot { get; set; }

        /// <summary>Gets or sets why the pages' snapshot is stale, or <see langword="null"/> when it is current.</summary>
        public string? Stale { get; set; }

        /// <summary>Gets or sets the time before which GitHub asked not to be read again.</summary>
        public DateTime QuietUntil { get; set; }

        /// <summary>Gets or sets how many pages have the review's event stream open.</summary>
        public int Watchers { get; set; }

        /// <summary>Gets or sets a value indicating whether a loop follows the review's pull request.</summary>
        public bool Watching { get; set; }

        /// <summary>Attaches the review to a pull request, as it stood when the review began.</summary>
        public void Attach(int number, PullSnapshot snapshot)
        {
            Pull = number;
            Snapshot = snapshot;
            OpenedCommits = snapshot.Commits;
        }

        /// <summary>Records the base commit of a pull request's review once its commits are present.</summary>
        public void Resolve(string baseCommit) => Base = baseCommit;

        /// <summary>Returns a task that completes at the next refresh request, replacing the one before.</summary>
        public Task NextPoke()
        {
            poke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return poke.Task;
        }

        /// <summary>Gets the current result together with its run, read at one moment.</summary>
        public (ReviewResult? Result, int Run) Current { get { lock (this) return (Result, Run); } }

        /// <summary>Records a new result, which starts a new run.</summary>
        public void Publish(ReviewResult result)
        {
            lock (this)
            {
                Result = result;
                Run++;
            }
        }

        /// <summary>Gets the number of events recorded so far.</summary>
        public int Count { get { lock (events) return events.Count; } }
        public string State => Failed ? "failed" : Result is not null ? "ready" : "running";

        /// <summary>Records an event with its sequence number, and wakes every stream waiting for one.</summary>
        public void Add(object e)
        {
            var node = JsonSerializer.SerializeToNode(e, FactsJson.Options)!.AsObject();
            TaskCompletionSource old;
            lock (events)
            {
                node["seq"] = events.Count;
                events.Add(node.ToJsonString(FactsJson.Options));
                old = signal;
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            old.TrySetResult();
        }

        /// <summary>Returns the events from an index on, and a task that completes when the next event is recorded.</summary>
        public (List<string> Events, Task Next) From(int index)
        {
            lock (events) return (events.Skip(index).ToList(), signal.Task);
        }
    }
}
