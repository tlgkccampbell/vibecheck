using System.Reflection;

namespace Vibecheck.Cli;

/// <summary>
/// Represents the note that tells coding agents in a repository about its map, and the guide that the note sends them
/// to. The guide is <c>docs/agent-guide.md</c>, embedded in the tool and printed by <c>vibecheck guide</c>, so that it
/// always describes the installed version. It says how to keep the map in sync and how to state a change's intent.
/// The note goes in the repository's agent instructions file, which <see cref="Target"/> chooses.
/// </summary>
internal static class AgentNote
{
    /// <summary>The name of Claude Code's instructions file.</summary>
    public const string Claude = "CLAUDE.md";

    /// <summary>The name of the instructions file that other coding agents read, and that a <c>CLAUDE.md</c> may import.</summary>
    public const string Agents = "AGENTS.md";

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
        ## vibecheck

        This repository's changes are reviewed with vibecheck, which draws each change on the
        architecture map `{mapPath}`. Before finishing a change, run `{Command}` and
        follow it. It says when the map needs updating, and how to state the intent of the
        change in the pull request description.

        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Returns the name of the instructions file in the given directory that the note belongs in. A <c>CLAUDE.md</c>
    /// that imports <c>AGENTS.md</c> sends the note there, so that every agent reads it. Otherwise an existing
    /// <c>CLAUDE.md</c> takes it, then an existing <c>AGENTS.md</c>, and with neither a new <c>CLAUDE.md</c> is created.
    /// </summary>
    public static string Target(string top)
    {
        var claude = Path.Combine(top, Claude);
        if (File.Exists(claude))
            return File.ReadAllLines(claude).Any(line => line.Trim() is "@AGENTS.md" or "@./AGENTS.md") ? Agents : Claude;
        return File.Exists(Path.Combine(top, Agents)) ? Agents : Claude;
    }

    /// <summary>
    /// Gets a value indicating whether either instructions file in the given directory already carries the note, so
    /// that the note is never added to one file while the other has it.
    /// </summary>
    public static bool IsPresent(string top) =>
        new[] { Claude, Agents }.Any(name => File.Exists(Path.Combine(top, name)) && File.ReadAllText(Path.Combine(top, name)).Contains(Command, StringComparison.Ordinal));

    /// <summary>
    /// Adds the note to the end of the given instructions file in the given directory, and creates the file when it is
    /// missing. The note takes the file's line endings. Returns <see langword="false"/> when either file already
    /// carries the note.
    /// </summary>
    public static bool AddTo(string top, string file, string mapPath)
    {
        if (IsPresent(top)) return false;
        var path = Path.Combine(top, file);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = existing.Length == 0 ? "" : existing.EndsWith('\n') ? newline : newline + newline;
        File.WriteAllText(path, existing + separator + Text(mapPath).ReplaceLineEndings(newline));
        return true;
    }
}
