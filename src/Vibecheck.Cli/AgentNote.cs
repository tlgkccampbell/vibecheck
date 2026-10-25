using System.Reflection;

namespace Vibecheck.Cli;

/// <summary>
/// Represents the note that tells coding agents in a repository about its map, and the guide that the note sends them
/// to. The guide is <c>docs/keeping-the-map-in-sync.md</c>, embedded in the tool and printed by <c>vibecheck guide</c>,
/// so that it always describes the installed version. The note goes in the repository's <c>CLAUDE.md</c>.
/// </summary>
internal static class AgentNote
{
    /// <summary>The name of the instructions file, at the repository's top, that the note is added to.</summary>
    public const string FileName = "CLAUDE.md";

    /// <summary>The command that the note names, which also tells whether a file already carries the note.</summary>
    private const string Command = "vibecheck guide";

    /// <summary>Returns the guide for coding agents, in Markdown.</summary>
    public static string Guide()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("guide.md") ?? throw new FileNotFoundException("the guide is not embedded", "guide.md");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Returns the note, as a Markdown section, for a map at the given path relative to the repository's top.</summary>
    public static string Text(string mapPath) => $"""
        ## Architecture map

        This repository has an architecture map, `{mapPath}`, that the vibecheck review tool draws changes with.
        Before finishing a change that adds, moves, renames or deletes types, namespaces, projects or package
        references, run `{Command}` and follow it to keep the map in sync.

        """.ReplaceLineEndings("\n");

    /// <summary>Gets a value indicating whether the instructions file in the given directory already carries the note.</summary>
    public static bool IsPresent(string top)
    {
        var path = Path.Combine(top, FileName);
        return File.Exists(path) && File.ReadAllText(path).Contains(Command, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds the note to the end of the instructions file in the given directory, and creates the file when it is
    /// missing. The note takes the file's line endings. Returns <see langword="false"/> when the file already carries it.
    /// </summary>
    public static bool AddTo(string top, string mapPath)
    {
        if (IsPresent(top)) return false;
        var path = Path.Combine(top, FileName);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = existing.Length == 0 ? "" : existing.EndsWith('\n') ? newline : newline + newline;
        File.WriteAllText(path, existing + separator + Text(mapPath).ReplaceLineEndings(newline));
        return true;
    }
}
