using System.Diagnostics;

namespace Archmap.Diffing;

/// <summary>Collects the textual diff of files between two revisions by running <c>git diff</c>, and parses it into hunks.</summary>
public static class GitHunks
{
    public static List<FileHunks> Collect(string repo, string baseRev, string headRev, IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return [];
        var args = new List<string> { "-C", repo, "diff", "--no-color", "--no-renames", "--unified=3", baseRev, headRev, "--" };
        args.AddRange(list);
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git diff failed: " + error.Trim());
        return Parse(output);
    }

    /// <summary>Parses unified diff text into per-file hunks.</summary>
    public static List<FileHunks> Parse(string text)
    {
        var result = new List<FileHunks>();
        FileHunks? current = null;
        Hunk? hunk = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                current = null; hunk = null;
                continue;
            }
            // The path comes from whichever side of the header is not /dev/null, so
            // that added files take the "+++" path and deleted files keep the "---" path.
            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = line[4..];
                if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)) path = path[2..];
                if (path != "/dev/null") current = new FileHunks(path, []);
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (current is null) continue;
                if (!result.Contains(current)) result.Add(current);
                hunk = new Hunk(line, []);
                current.Hunks.Add(hunk);
                continue;
            }
            if (hunk is null) continue;
            if (line.StartsWith("\\ No newline", StringComparison.Ordinal)) continue;
            if (line.Length == 0) { hunk.Lines.Add([" ", ""]); continue; }
            var marker = line[0] switch { '+' => "+", '-' => "-", _ => " " };
            hunk.Lines.Add([marker, line[1..]]);
        }
        return result.Where(f => f.Path.Length > 0).ToList();
    }
}
