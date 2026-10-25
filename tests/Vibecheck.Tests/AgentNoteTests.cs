using Vibecheck.Cli;
using Xunit;

namespace Vibecheck.Tests;

public class AgentNoteTests
{
    [Fact]
    public void TheGuideIsEmbeddedInTheTool()
    {
        Assert.StartsWith("# vibecheck for coding agents", AgentNote.Guide(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoteNamesTheMapTheGuideCommandAndThePullRequestDescription()
    {
        var text = AgentNote.Text("src/app/vibecheck.json");
        Assert.Contains("`src/app/vibecheck.json`", text, StringComparison.Ordinal);
        Assert.Contains("`vibecheck guide`", text, StringComparison.Ordinal);
        Assert.Contains("pull request description", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingTheNoteCreatesTheFileWhenItIsMissing()
    {
        using var temp = new TempDirectory();
        Assert.False(AgentNote.IsPresent(temp.Path));
        Assert.Equal("CLAUDE.md", AgentNote.Target(temp.Path));
        Assert.True(AgentNote.AddTo(temp.Path, "CLAUDE.md", "vibecheck.json"));
        Assert.Equal(AgentNote.Text("vibecheck.json"), File.ReadAllText(Path.Combine(temp.Path, "CLAUDE.md")));
        Assert.True(AgentNote.IsPresent(temp.Path));
    }

    [Fact]
    public void TheNoteIsAppendedAfterABlankLineWithTheFilesLineEndingsAndOnlyOnce()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "CLAUDE.md");
        File.WriteAllText(path, "# Project\r\n\r\nBuild with make.");

        Assert.True(AgentNote.AddTo(temp.Path, "CLAUDE.md", "vibecheck.json"));
        var text = File.ReadAllText(path);
        Assert.StartsWith("# Project\r\n\r\nBuild with make.\r\n\r\n## vibecheck\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // A file that already sends agents to the guide is left alone.
        Assert.False(AgentNote.AddTo(temp.Path, "CLAUDE.md", "vibecheck.json"));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("@AGENTS.md\n", true, "AGENTS.md")]
    [InlineData("# Claude\n\n  @./AGENTS.md  \n\nUse tabs.\n", true, "AGENTS.md")]
    [InlineData("See @AGENTS.md for the rest.\n", true, "CLAUDE.md")]
    [InlineData("# Claude\n", true, "CLAUDE.md")]
    [InlineData(null, true, "AGENTS.md")]
    [InlineData(null, false, "CLAUDE.md")]
    public void TheNoteGoesWhereTheRepositorysInstructionsLive(string? claude, bool agents, string expected)
    {
        using var temp = new TempDirectory();
        if (claude is not null) File.WriteAllText(Path.Combine(temp.Path, "CLAUDE.md"), claude);
        if (agents) File.WriteAllText(Path.Combine(temp.Path, "AGENTS.md"), "# Agents\n");
        Assert.Equal(expected, AgentNote.Target(temp.Path));
    }

    [Fact]
    public void AFileWithTheEarlierNoteIsNotGivenTheNewOne()
    {
        using var temp = new TempDirectory();
        var earlier = "## Architecture map\n\nBefore finishing a change that moves types, run `vibecheck guide` and follow it to keep the map in sync.\n";
        File.WriteAllText(Path.Combine(temp.Path, "CLAUDE.md"), earlier);
        Assert.True(AgentNote.IsPresent(temp.Path));
        Assert.False(AgentNote.AddTo(temp.Path, "CLAUDE.md", "vibecheck.json"));
        Assert.Equal(earlier, File.ReadAllText(Path.Combine(temp.Path, "CLAUDE.md")));
    }

    [Fact]
    public void TheNoteInEitherFileCountsAsPresent()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "CLAUDE.md"), "# Claude\n");
        File.WriteAllText(Path.Combine(temp.Path, "AGENTS.md"), "Run `vibecheck guide` first.\n");
        Assert.True(AgentNote.IsPresent(temp.Path));
        Assert.False(AgentNote.AddTo(temp.Path, "CLAUDE.md", "vibecheck.json"));
        Assert.Equal("# Claude\n", File.ReadAllText(Path.Combine(temp.Path, "CLAUDE.md")));
    }
}
