using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archmap.Checking;
using Archmap.Diffing;
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
        var running = reviews.Values.Select(r => r.Task).Where(t => t is not null).Cast<Task>().ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(15)));
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        stopping.Dispose();
    }

    private Review Start(Review review)
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
        options.Log?.Invoke($"reviewing {review.Label} ({review.Base[..8]}..{review.Head[..8]})");
        review.Add(new { type = "review", label = review.Label, @base = review.Base, head = review.Head });
        review.Task = Task.Run(async () =>
        {
            try
            {
                review.Result = await ReviewPipeline.RunAsync(request, options.Analyze, options.Cache, progress, stopping.Token);
                foreach (var w in review.Result.Info.Warnings) options.Log?.Invoke($"  {review.Id} warning: {w}");
                options.Log?.Invoke($"  {review.Id} ready");
                review.Add(new { type = "ready", url = $"/reviews/{review.Id}/" });
            }
            catch (Exception ex)
            {
                review.Failed = true;
                var message = ex is OperationCanceledException ? "cancelled" : ex.Message;
                options.Log?.Invoke($"  {review.Id} failed: {message}");
                review.Add(new { type = "failed", message });
            }
        });
        return review;
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
            if (!reviews.TryGetValue(id, out var r) || r.Result is null) return Results.NotFound();
            var info = r.Result.Info;
            var meta = new { solution = info.Solution, title = info.Title ?? r.Label, range = info.Range, map = info.MapSource, mapKind = info.MapKind, warnings = info.Warnings, home = "/" };
            var script = $"window.CHANGESET_META = {JsonSerializer.Serialize(meta, FactsJson.Options)};\nwindow.CHANGESET = {FactsJson.Serialize(r.Result.Changeset)}";
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
        public ReviewResult? Result { get; set; }
        public bool Failed { get; set; }
        public string State => Failed ? "failed" : Result is not null ? "ready" : "running";

        /// <summary>Records an event and wakes every stream waiting for one.</summary>
        public void Add(object e)
        {
            var json = JsonSerializer.Serialize(e, FactsJson.Options);
            TaskCompletionSource old;
            lock (events)
            {
                events.Add(json);
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
