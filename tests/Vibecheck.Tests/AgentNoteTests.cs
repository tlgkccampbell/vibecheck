using Vibecheck.Cli;
using Xunit;

namespace Vibecheck.Tests;

public class AgentNoteTests
{
    [Fact]
    public void TheGuideIsEmbeddedInTheTool()
    {
        Assert.StartsWith("# Keeping `vibecheck.json` in sync", AgentNote.Guide(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoteNamesTheMapAndTheGuideCommand()
    {
        var text = AgentNote.Text("src/app/vibecheck.json");
        Assert.Contains("`src/app/vibecheck.json`", text, StringComparison.Ordinal);
        Assert.Contains("`vibecheck guide`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingTheNoteCreatesTheFileWhenItIsMissing()
    {
        using var temp = new TempDirectory();
        Assert.False(AgentNote.IsPresent(temp.Path));
        Assert.True(AgentNote.AddTo(temp.Path, "vibecheck.json"));
        Assert.Equal(AgentNote.Text("vibecheck.json"), File.ReadAllText(Path.Combine(temp.Path, "CLAUDE.md")));
        Assert.True(AgentNote.IsPresent(temp.Path));
    }

    [Fact]
    public void TheNoteIsAppendedAfterABlankLineWithTheFilesLineEndingsAndOnlyOnce()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "CLAUDE.md");
        File.WriteAllText(path, "# Project\r\n\r\nBuild with make.");

        Assert.True(AgentNote.AddTo(temp.Path, "vibecheck.json"));
        var text = File.ReadAllText(path);
        Assert.StartsWith("# Project\r\n\r\nBuild with make.\r\n\r\n## Architecture map\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // A file that already sends agents to the guide is left alone.
        Assert.False(AgentNote.AddTo(temp.Path, "vibecheck.json"));
        Assert.Equal(text, File.ReadAllText(path));
    }
}
