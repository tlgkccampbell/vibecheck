using System.Text.RegularExpressions;

namespace Vibecheck.GitHub;

/// <summary>
/// Reads the end of a GitHub Actions job's log, which is where a failed job says why it failed. See
/// <c>docs/pull-requests.md</c> section 9.
/// </summary>
public static partial class JobLogs
{
    /// <summary>
    /// Returns the last lines of a job's log as a reader wants them: without the time that GitHub puts before each line,
    /// without terminal color codes, and without the blank lines at the end.
    /// </summary>
    public static List<string> Tail(string log, int count)
    {
        var lines = log.Replace("\r\n", "\n").Split('\n').Select(l => Color().Replace(Stamp().Replace(l, ""), "")).ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
    }

    [GeneratedRegex(@"^\uFEFF?\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z ")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Color();
}
