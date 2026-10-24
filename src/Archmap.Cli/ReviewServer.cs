using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archmap.Assist;
using Archmap.Checking;
using Archmap.Diffing;
using Archmap.Model;
using Archmap.Reviewing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Archmap.Cli;

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

    /// <summary>Gets a value indicating whether the gh command is asked for open pull requests.</summary>
    public bool PullRequests { get; init; } = true;

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
    private WebApplication? app;
    private int port;

    public ReviewServer(ReviewServerOptions options) => this.options = options;

    /// <summary>Gets the address the server listens on, without the token.</summary>
    public string BaseAddress => $"http://127.0.0.1:{port}";

    /// <summary>Gets the name of the cookie that carries the token. It names the port, since browsers share cookies across the ports of one host.</summary>
    private string CookieName => $"archmap-{port}";

    /// <summary>Starts listening and returns the URL that opens the picker and sets the token cookie.</summary>
    public async Task<string> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        app = builder.Build();
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
        options.Log?.Invoke($"reviewing {review.Label} ({review.Base[..8]}..{review.Head[..8]})");
        review.Add(new { type = "review", label = review.Label, @base = review.Base, head = review.Head });
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
        var request = new ReviewRequest(options.Directory, review.Base, review.Head)
        {
            MapPath = options.MapPath, Root = options.Root, Solution = options.Solution, Build = options.Build,
        };
        try
        {
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
    /// Starts the map work a finished review calls for: generating a concept map when it was drawn with the skeleton,
    /// and placing the unsorted types when it was drawn with a generated map that leaves some. Reviews of the same
    /// solution share one job, and each is told when the job finishes. See <c>docs/review-workflow.md</c> sections 4.4
    /// and 4.5.
    /// </summary>
    private void ImproveMap(Review review, ReviewResult result)
    {
        var kind = result.Info.MapKind;
        var unsorted = result.Changeset.Types.Any(t => t.LeafHead == ArchMap.Unsorted || t.LeafBase == ArchMap.Unsorted);
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
                job.Context = new NarrativeContext(result.Changeset, result.BaseAnalysis, result.HeadAnalysis, NarrativeContext.ReadCommits(top, review.Base, review.Head))
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
    /// Writes a review's generated map into the repository as <c>archmap.json</c> beside the solution, where reviews of
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
    /// token in its query sets the cookie and is sent on without it. A POST must send JSON, which a cross-site form cannot.
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
            await ctx.Response.WriteAsync("Open the link that the archmap command printed.");
            return;
        }
        if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
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
        web.MapGet("/", () => Page("start.html", "text/html; charset=utf-8"));

        web.MapGet("/api/ranges", async () =>
        {
            var ranges = RangeCatalog.List(options.Directory);
            if (options.PullRequests) ranges.AddRange(await RangeCatalog.PullRequestsAsync(options.Directory, TimeSpan.FromSeconds(4)));
            var top = Git.Run(options.Directory, "rev-parse", "--show-toplevel");
            var open = reviews.Values.OrderBy(r => r.Started).Select(r => new { r.Id, r.Label, state = r.State, url = $"/reviews/{r.Id}/" });
            return Results.Json(new { repository = Path.GetFileName(top), ranges, reviews = open }, FactsJson.Options);
        });

        web.MapPost("/api/reviews", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<BeginBody>(ctx.Request.Body, FactsJson.Options);
            if (string.IsNullOrWhiteSpace(body?.Range)) return Results.BadRequest(new { message = "a range is required" });
            try
            {
                var url = Begin(body.Range.Trim(), body.Label);
                return Results.Json(new { id = url.Split('/')[2], url }, FactsJson.Options);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        web.MapGet("/api/reviews/{id}/events", async (string id, HttpContext ctx) =>
        {
            if (!reviews.TryGetValue(id, out var review)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var ct = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping.Token).Token;
            var sent = 0;
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
            return Results.Text(page, "text/html; charset=utf-8");
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
                narratives = options.Claude is not null,
            };
            var script = $"window.CHANGESET_META = {JsonSerializer.Serialize(meta, FactsJson.Options)};\nwindow.CHANGESET = {FactsJson.Serialize(result.Changeset)}";
            return Results.Text(script, "text/javascript; charset=utf-8");
        });
    }

    private IResult Page(string name, string contentType) => Results.Text(ReadViewer(name), contentType);

    /// <summary>Returns one of the viewer's files, from the viewer directory when the options name one and from the tool's embedded copy otherwise.</summary>
    private string ReadViewer(string name)
    {
        if (options.ViewerDirectory is not null) return File.ReadAllText(Path.Combine(options.ViewerDirectory, name));
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("viewer/" + name) ?? throw new FileNotFoundException("the viewer file is not embedded", name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record BeginBody(string? Range, string? Label);

    private sealed record MapBody(string? Action);

    private sealed record NarrativesBody(string? Key);

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
    private sealed class Review(string id, string baseCommit, string headCommit, string label)
    {
        private readonly List<string> events = [];
        private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;
        public string Base { get; } = baseCommit;
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
