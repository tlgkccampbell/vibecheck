using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vibecheck.Assist;

/// <summary>
/// Describes one request to Claude: the prompt, the JSON Schema the answer must follow, the directory Claude works in,
/// the read-only tools it may use there, and the model, where the default is not wanted.
/// </summary>
public sealed record ClaudeRequest(string Prompt, string Schema)
{
    /// <summary>Gets the directory Claude works in, or <see langword="null"/> for an empty temporary one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the built-in tools Claude may use, such as <c>Read</c>, <c>Grep</c> and <c>Glob</c>. None by default.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Gets the model to use, or <see langword="null"/> for the Claude Code default.</summary>
    public string? Model { get; init; }

    /// <summary>Gets how long the request may take before it is abandoned as failed, or <see langword="null"/> for no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets a callback that receives what the request is doing every few seconds while it runs, or <see langword="null"/> for none.</summary>
    public Action<ClaudeActivity>? Activity { get; init; }
}

/// <summary>
/// Reports what a running request to Claude has done so far: how long it has run, and about how many tokens it has
/// spent thinking and writing its answer. Claude Code reports only the amount of thinking, not its text.
/// </summary>
public sealed record ClaudeActivity(TimeSpan Elapsed, int Thinking, int Writing)
{
    /// <summary>Returns a phrase that says what the request is doing, such as "thinking, about 9,400 tokens so far (3m12s)".</summary>
    public string Describe()
    {
        var time = FormattableString.Invariant($"{(int)Elapsed.TotalMinutes}m{Elapsed.Seconds:00}s");
        if (Writing > 0 && Thinking > 0) return FormattableString.Invariant($"writing the answer, about {Writing:N0} tokens so far after about {Thinking:N0} tokens of thinking ({time})");
        if (Writing > 0) return FormattableString.Invariant($"writing the answer, about {Writing:N0} tokens so far ({time})");
        if (Thinking > 0) return FormattableString.Invariant($"thinking, about {Thinking:N0} tokens so far ({time})");
        return $"waiting for Claude to start ({time})";
    }
}

/// <summary>Asks Claude for an answer of a known shape. <see cref="ClaudeCli"/> is the real one, and tests supply canned answers.</summary>
public interface IClaude
{
    /// <summary>Sends a request and returns the answer, which follows the request's schema.</summary>
    /// <exception cref="ClaudeException">Claude could not be started, failed, took longer than the request allows, or gave no structured answer.</exception>
    Task<JsonElement> AskAsync(ClaudeRequest request, CancellationToken ct = default);
}

/// <summary>Reports that a request to Claude failed.</summary>
public sealed class ClaudeException(string message) : Exception(message);

/// <summary>
/// Calls the Claude Code command line in print mode, on the reviewer's own Claude account. Each call is restricted: it
/// loads no user or project settings and no MCP servers, keeps no session, and has only the tools the request names,
/// confined to its working directory. The call's events are read as they arrive, so that a request that asks for its
/// activity can report it while it runs. Each answered call is reported to the log, if one is given, with the model
/// that answered, the time it took and its cost. See <c>docs/review-workflow.md</c> section 7.
/// </summary>
public sealed class ClaudeCli(string executable = "claude", Action<string>? log = null) : IClaude
{
    /// <summary>Gets how often a request that asks for its activity is sent it.</summary>
    public TimeSpan ActivityInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Returns the version the command line reports, or <see langword="null"/> when it cannot be started.</summary>
    public async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        try
        {
            var output = new StringBuilder();
            var (code, _) = await RunAsync(["--version"], null, null, l => output.AppendLine(l), ct);
            return code == 0 ? output.ToString().Trim() : null;
        }
        catch (ClaudeException)
        {
            return null;
        }
    }

    public async Task<JsonElement> AskAsync(ClaudeRequest request, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose", "--json-schema", request.Schema, "--no-session-persistence",
            "--restricted", "--strict-mcp-config", "--tools", string.Join(',', request.Tools),
        };
        // Partial messages carry the thinking and answer deltas that the activity counts.
        if (request.Activity is not null) args.Add("--include-partial-messages");
        if (request.Tools.Count > 0) args.AddRange(["--allowedTools", string.Join(',', request.Tools)]);
        if (request.Model is not null) args.AddRange(["--model", request.Model]);

        var empty = request.WorkingDirectory is null ? Directory.CreateTempSubdirectory("vibecheck-claude-").FullName : null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (request.Timeout is { } timeout) limit.CancelAfter(timeout);
            var stream = new ClaudeStream();
            var clock = Stopwatch.StartNew();
            var ticker = new PeriodicTimer(ActivityInterval);
            var ticking = request.Activity is { } activity ? TickAsync(ticker, () => activity(stream.Activity(clock.Elapsed))) : Task.CompletedTask;
            int code;
            string error;
            try
            {
                (code, error) = await RunAsync(args, request.Prompt, request.WorkingDirectory ?? empty, stream.Read, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ClaudeException($"claude gave no answer within {request.Timeout!.Value.TotalSeconds:F0} s");
            }
            finally
            {
                ticker.Dispose();
                await ticking;
            }
            if (stream.Result is not { } envelope)
                throw new ClaudeException($"claude exited with {code} and no answer: {Tail(error.Length > 0 ? error : stream.Unparsed)}");
            if (envelope.TryGetProperty("is_error", out var isError) && isError.GetBoolean())
            {
                var result = envelope.TryGetProperty("result", out var r) ? r.ToString() : "";
                throw new ClaudeException("claude failed: " + Tail(result));
            }
            log?.Invoke(Describe(envelope));
            if (!envelope.TryGetProperty("structured_output", out var answer) || answer.ValueKind != JsonValueKind.Object)
                throw new ClaudeException("claude gave no answer in the requested shape");
            return answer;
        }
        finally
        {
            if (empty is not null) Directory.Delete(empty, recursive: true);
        }
    }

    /// <summary>Calls an action on every tick of a timer until the timer is disposed.</summary>
    private static async Task TickAsync(PeriodicTimer ticker, Action action)
    {
        while (await ticker.WaitForNextTickAsync()) action();
    }

    /// <summary>Runs the command line, passes each line it writes to standard output to a callback, and returns its exit code and standard error.</summary>
    private async Task<(int Code, string Error)> RunAsync(IEnumerable<string> args, string? input, string? dir, Action<string> line, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        if (dir is not null) psi.WorkingDirectory = dir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new ClaudeException("claude could not be started");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ClaudeException("claude could not be started: " + ex.Message);
        }
        using (process)
        {
            var output = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } l) line(l);
            }, CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            await output;
            return (process.ExitCode, await error);
        }
    }

    /// <summary>Returns a line naming the models that answered a call, how long it took and what it cost, from the call's result envelope.</summary>
    private static string Describe(JsonElement envelope)
    {
        var models = envelope.TryGetProperty("modelUsage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? string.Join(", ", usage.EnumerateObject().Select(p => p.Name)) : "an unknown model";
        var seconds = envelope.TryGetProperty("duration_ms", out var ms) && ms.TryGetDouble(out var d) ? d / 1000 : 0;
        var cost = envelope.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var usd) ? usd : 0;
        return $"claude: {models}, {seconds:F0} s, ${cost:F3}";
    }

    private static string Tail(string text) => text.Length <= 600 ? text.Trim() : "…" + text[^600..].Trim();
}

/// <summary>
/// Reads the events that <c>claude -p --output-format stream-json</c> writes, one JSON object to a line. It keeps the
/// final result event, which carries the answer, and counts the tokens spent so far on thinking and on the answer. It
/// may be read from one thread while another writes to it.
/// </summary>
internal sealed class ClaudeStream
{
    private readonly Lock gate = new();
    private readonly StringBuilder unparsed = new();
    private int thinkingBefore, thinking, written;

    /// <summary>Gets the result event, or <see langword="null"/> until it has been read.</summary>
    public JsonElement? Result { get; private set; }

    /// <summary>Gets the lines that were not events, such as a message the command line wrote before it failed.</summary>
    public string Unparsed
    {
        get { lock (gate) return unparsed.ToString(); }
    }

    /// <summary>Reads one line of the stream.</summary>
    public void Read(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            lock (gate) unparsed.AppendLine(line);
            return;
        }
        using var _ = doc;
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type)) return;
        if (type.ValueEquals("result"))
        {
            Result = e.Clone();
            return;
        }
        if (!type.ValueEquals("stream_event") || !e.TryGetProperty("event", out var ev) || !ev.TryGetProperty("type", out var kind)) return;
        lock (gate)
        {
            // Each delta of a thinking block carries the block's estimated size so far, so a new block starts a new count.
            if (kind.ValueEquals("content_block_start") && ev.TryGetProperty("content_block", out var block) && block.TryGetProperty("type", out var bt) && bt.ValueEquals("thinking"))
            {
                thinkingBefore += thinking;
                thinking = 0;
            }
            else if (kind.ValueEquals("content_block_delta") && ev.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var dt))
            {
                if (dt.ValueEquals("thinking_delta") && delta.TryGetProperty("estimated_tokens", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var tokens)) thinking = Math.Max(thinking, tokens);
                else if (dt.ValueEquals("text_delta") && delta.TryGetProperty("text", out var t)) written += t.GetString()!.Length;
                else if (dt.ValueEquals("input_json_delta") && delta.TryGetProperty("partial_json", out var j)) written += j.GetString()!.Length;
            }
        }
    }

    /// <summary>Returns the activity read so far, counting about four characters of the answer as a token.</summary>
    public ClaudeActivity Activity(TimeSpan elapsed)
    {
        lock (gate) return new ClaudeActivity(elapsed, thinkingBefore + thinking, written / 4);
    }
}
