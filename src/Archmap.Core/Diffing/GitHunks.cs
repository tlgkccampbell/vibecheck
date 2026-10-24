using System.Diagnostics;

namespace Archmap.Diffing;

/// <summary>
/// Collects the textual diff of files between two revisions by running <c>git diff</c>, and parses it into hunks. Each
/// C# file present at both revisions has its hunks marked by <see cref="TrivialEdits"/>.
/// </summary>
public static class GitHunks
{
    /// <summary>Returns the hunks of the given files between two revisions, with trivial edits marked.</summary>
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
        var files = Parse(output);
        var marked = files.Where(f => TrivialEdits.Applies(f.Path)).ToList();
        var texts = ReadBlobs(repo, marked.SelectMany(f => new[] { baseRev + ":" + f.Path, headRev + ":" + f.Path }).ToList());
        return files.Select(f =>
            texts.TryGetValue(baseRev + ":" + f.Path, out var before) && texts.TryGetValue(headRev + ":" + f.Path, out var after)
                ? TrivialEdits.Mark(f, before, after) : f).ToList();
    }

    /// <summary>
    /// Returns the contents of files at revisions, keyed by the <c>revision:path</c> names given, read in one
    /// <c>git cat-file --batch</c> process. A name that does not exist, such as a file added at head, is left out.
    /// </summary>
    private static Dictionary<string, string> ReadBlobs(string repo, List<string> names)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (names.Count == 0) return result;
        var psi = new ProcessStartInfo("git") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-C", repo, "cat-file", "--batch" }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        // The names go in on another thread, so that a full output pipe cannot stall the writer.
        var writer = Task.Run(() =>
        {
            foreach (var n in names) process.StandardInput.Write(n + "\n");
            process.StandardInput.Close();
        });
        var output = process.StandardOutput.BaseStream;
        foreach (var name in names)
        {
            var header = ReadLine(output);
            if (header.EndsWith(" missing", StringComparison.Ordinal)) continue;
            var size = int.Parse(header[(header.LastIndexOf(' ') + 1)..]);
            var bytes = new byte[size];
            output.ReadExactly(bytes);
            output.ReadByte();
            result[name] = System.Text.Encoding.UTF8.GetString(bytes);
        }
        writer.Wait();
        process.WaitForExit();
        return result;
    }

    /// <summary>Reads bytes up to a line feed and returns them as text, without the line feed.</summary>
    private static string ReadLine(Stream stream)
    {
        var bytes = new List<byte>();
        for (int b; (b = stream.ReadByte()) is not -1 and not '\n';) bytes.Add((byte)b);
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
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
