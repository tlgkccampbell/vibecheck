using Vibecheck.Assist;
using Xunit;

namespace Vibecheck.Tests;

public class ClaudeCliTests
{
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
}
