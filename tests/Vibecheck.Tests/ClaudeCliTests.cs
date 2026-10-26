using Vibecheck.Assist;
using Xunit;

namespace Vibecheck.Tests;

public class ClaudeCliTests
{
    // Events in the shape that claude -p --output-format stream-json --include-partial-messages writes, cut down.
    private const string FirstThinking = """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}}""";
    private const string Thought = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"","estimated_tokens":1200}}}""";
    private const string ThoughtEnd = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"","estimated_tokens":null}}}""";
    private const string SecondThinking = """{"type":"stream_event","event":{"type":"content_block_start","index":2,"content_block":{"type":"thinking","thinking":"","signature":""}}}""";
    private const string SecondThought = """{"type":"stream_event","event":{"type":"content_block_delta","index":2,"delta":{"type":"thinking_delta","thinking":"","estimated_tokens":300}}}""";
    private const string Answer = """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"primes\": [101, 103, 107]}"}}}""";
    private const string Result = """{"type":"result","is_error":false,"duration_ms":1249,"total_cost_usd":0.01,"modelUsage":{"claude-sonnet-5-5":{}},"structured_output":{"primes":[101,103,107]}}""";

    [Fact]
    public async Task AbandonsARequestThatTakesLongerThanItsTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        // A stand-in for the claude command that never answers.
        var hang = Path.Combine(temp.Path, "hang.cmd");
        File.WriteAllText(hang, "@ping -n 30 127.0.0.1 >nul\r\n");
        var claude = new ClaudeCli(hang);
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<ClaudeException>(() => claude.AskAsync(new ClaudeRequest("prompt", "{}") { Timeout = TimeSpan.FromSeconds(1) }));

        Assert.Contains("no answer within 1 s", ex.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReadsTheAnswerFromTheStreamAndReportsTheActivityWhileItRuns()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        File.WriteAllLines(Path.Combine(temp.Path, "events.jsonl"), [FirstThinking, Thought, Answer]);
        File.WriteAllLines(Path.Combine(temp.Path, "result.jsonl"), [Result]);
        // A stand-in for the claude command that thinks, writes, pauses and then reports its result.
        var slow = Path.Combine(temp.Path, "slow.cmd");
        File.WriteAllText(slow, "@type \"%~dp0events.jsonl\"\r\n@ping -n 3 127.0.0.1 >nul\r\n@type \"%~dp0result.jsonl\"\r\n");
        var claude = new ClaudeCli(slow) { ActivityInterval = TimeSpan.FromMilliseconds(200) };
        var reports = new List<ClaudeActivity>();

        var answer = await claude.AskAsync(new ClaudeRequest("prompt", "{}") { Activity = a => { lock (reports) reports.Add(a); } });

        Assert.Equal(3, answer.GetProperty("primes").GetArrayLength());
        lock (reports) Assert.Contains(reports, a => a is { Thinking: 1200, Writing: 6 });
    }

    [Fact]
    public void CountsTheThinkingOfEveryBlockAndTheAnswerWritten()
    {
        var stream = new ClaudeStream();

        foreach (var line in new[] { FirstThinking, Thought, ThoughtEnd, Answer, SecondThinking, SecondThought, "not an event", Result }) stream.Read(line);

        Assert.Equal(new ClaudeActivity(TimeSpan.FromSeconds(75), 1500, 6), stream.Activity(TimeSpan.FromSeconds(75)));
        Assert.Equal(1249, stream.Result!.Value.GetProperty("duration_ms").GetInt32());
        Assert.Equal("not an event", stream.Unparsed.Trim());
    }

    [Theory]
    [InlineData(0, 0, "waiting for Claude to start (0m05s)")]
    [InlineData(9400, 0, "thinking, about 9,400 tokens so far (0m05s)")]
    [InlineData(9400, 2100, "writing the answer, about 2,100 tokens so far after about 9,400 tokens of thinking (0m05s)")]
    [InlineData(0, 2100, "writing the answer, about 2,100 tokens so far (0m05s)")]
    public void DescribesWhatARequestIsDoing(int thinking, int writing, string expected) =>
        Assert.Equal(expected, new ClaudeActivity(TimeSpan.FromSeconds(5), thinking, writing).Describe());
}
