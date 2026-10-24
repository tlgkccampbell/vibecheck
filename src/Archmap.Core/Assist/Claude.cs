using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Archmap.Assist;

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
/// confined to its working directory. Each answered call is reported to the log, if one is given, with the model that
/// answered, the time it took and its cost. See <c>docs/review-workflow.md</c> section 7.
/// </summary>
public sealed class ClaudeCli(string executable = "claude", Action<string>? log = null) : IClaude
{
    /// <summary>Returns the version the command line reports, or <see langword="null"/> when it cannot be started.</summary>
    public async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        try
        {
            var (code, output, _) = await RunAsync(["--version"], null, null, ct);
            return code == 0 ? output.Trim() : null;
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
            "-p", "--output-format", "json", "--json-schema", request.Schema, "--no-session-persistence",
            "--restricted", "--strict-mcp-config", "--tools", string.Join(',', request.Tools),
        };
        if (request.Tools.Count > 0) args.AddRange(["--allowedTools", string.Join(',', request.Tools)]);
        if (request.Model is not null) args.AddRange(["--model", request.Model]);

        var empty = request.WorkingDirectory is null ? Directory.CreateTempSubdirectory("archmap-claude-").FullName : null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (request.Timeout is { } timeout) limit.CancelAfter(timeout);
            int code;
            string output, error;
            try
            {
                (code, output, error) = await RunAsync(args, request.Prompt, request.WorkingDirectory ?? empty, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ClaudeException($"claude gave no answer within {request.Timeout!.Value.TotalSeconds:F0} s");
            }
            JsonElement envelope;
            try
            {
                envelope = JsonDocument.Parse(output).RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new ClaudeException($"claude exited with {code} and no answer: {Tail(error.Length > 0 ? error : output)}");
            }
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

    private async Task<(int Code, string Output, string Error)> RunAsync(IEnumerable<string> args, string? input, string? dir, CancellationToken ct)
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
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
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
            return (process.ExitCode, await output, await error);
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
