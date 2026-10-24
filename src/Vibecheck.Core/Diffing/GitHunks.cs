using System.Diagnostics;

namespace Vibecheck.Diffing;

/// <summary>
/// A file that git reports as changed between two revisions. The status is <c>added</c>, <c>removed</c>,
/// <c>modified</c> or <c>renamed</c>. The path is the file's path at head, or at base for a removed file, and a renamed
/// file also has its path at base. The blobs are the file's object identifiers at each end, null at an end where the file
/// does not exist.
/// </summary>
public sealed record GitChange(string Path, string Status, string? PreviousPath, string? BlobBase, string? BlobHead)
{
    /// <summary>
    /// Gets a value indicating whether <see cref="GitHunks.Reconcile"/> paired the file differently from git, so that
    /// its hunks are diffed on their own rather than taken from git's diff of the whole range.
    /// </summary>
    public bool Repaired { get; init; }
}

/// <summary>
/// Collects the textual diff of files between two revisions by running <c>git diff</c>, and parses it into hunks. Every
/// diff detects renames with git's default similarity, and has three lines of context, as GitHub's does. Each C# file
/// present at both revisions has its hunks marked by <see cref="TrivialEdits"/>.
/// </summary>
public static class GitHunks
{
    // Every diff names its options in full, so that the reviewer's git configuration cannot change what is parsed.
    private static readonly string[] DiffOptions = ["-c", "core.quotePath=false", "diff", "--no-color", "--find-renames", "--relative", "--no-ext-diff"];

    /// <summary>
    /// Returns every file that changed between two revisions, with renames paired. The paths are relative to
    /// <paramref name="repo"/>, which may be any directory inside the repository, and files outside it are left out.
    /// </summary>
    public static List<GitChange> Changes(string repo, string baseRev, string headRev)
    {
        var fields = Git.RunExact(repo, [.. DiffOptions, "--raw", "-z", "--no-abbrev", baseRev, headRev]).Split('\0');
        var result = new List<GitChange>();
        // Each entry is a header such as ":100644 100644 <blob> <blob> M" followed by one path, or two for a rename.
        for (var i = 0; i + 1 < fields.Length; i++)
        {
            if (!fields[i].StartsWith(':')) continue;
            var header = fields[i].Split(' ');
            var (blobBase, blobHead, letter) = (Blob(header[2]), Blob(header[3]), header[4][0]);
            if (letter == 'R')
            {
                result.Add(new GitChange(fields[i + 2], "renamed", fields[i + 1], blobBase, blobHead));
                i += 2;
                continue;
            }
            var status = letter switch { 'A' => "added", 'D' => "removed", _ => "modified" };
            result.Add(new GitChange(fields[i + 1], status, null, blobBase, blobHead));
            i++;
        }
        return result;
    }

    /// <summary>
    /// Returns the changes with renames paired as another source pairs them, such as a pull request's file list on
    /// GitHub. A rename that git found and the pairs do not name is split into the removal of its base path and the
    /// addition of its head path. An addition and a removal that the pairs name as one rename are joined. Entries that
    /// change are marked <see cref="GitChange.Repaired"/>.
    /// </summary>
    /// <param name="changes">The changes as <see cref="Changes"/> returns them.</param>
    /// <param name="pairs">The renames by head path, each with its base path, relative to the same directory as the changes.</param>
    public static List<GitChange> Reconcile(IReadOnlyList<GitChange> changes, IReadOnlyDictionary<string, string> pairs)
    {
        var result = new List<GitChange>();
        foreach (var c in changes)
        {
            if (c.Status == "renamed" && !(pairs.TryGetValue(c.Path, out var old) && old == c.PreviousPath))
            {
                result.Add(new GitChange(c.PreviousPath!, "removed", null, c.BlobBase, null) { Repaired = true });
                result.Add(new GitChange(c.Path, "added", null, null, c.BlobHead) { Repaired = true });
            }
            else result.Add(c);
        }
        foreach (var (head, old) in pairs)
        {
            var added = result.FirstOrDefault(c => c.Path == head && c.Status == "added");
            var removed = result.FirstOrDefault(c => c.Path == old && c.Status == "removed");
            if (added is null || removed is null) continue;
            result.Remove(added);
            result.Remove(removed);
            result.Add(new GitChange(head, "renamed", old, removed.BlobBase, added.BlobHead) { Repaired = true });
        }
        return result.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Returns a blob identifier, or null for git's all-zero identifier, which names no object.</summary>
    private static string? Blob(string id) => id.Trim('0').Length == 0 ? null : id;

    /// <summary>
    /// Returns the hunks of every file that changed between two revisions, with trivial edits marked. The changes are
    /// those that <see cref="Changes"/> returns for the same revisions. The paths returned are relative to
    /// <paramref name="repo"/>, which may be any directory inside the repository, and each file's hunks are keyed by
    /// its path at head, or at base for a removed file.
    /// </summary>
    public static List<FileHunks> Collect(string repo, string baseRev, string headRev, IReadOnlyList<GitChange> changes)
    {
        if (changes.Count == 0) return [];
        var output = Git.RunExact(repo, [.. DiffOptions, "--unified=3", "--src-prefix=a/", "--dst-prefix=b/", baseRev, headRev]);
        var files = Parse(output);
        var repaired = changes.Where(c => c.Repaired).ToList();
        if (repaired.Count > 0)
        {
            // Git's diff of the range pairs these files its own way, so each is diffed on its own as the changes pair it.
            var touched = repaired.SelectMany(c => new[] { c.Path, c.PreviousPath ?? c.Path }).ToHashSet(StringComparer.Ordinal);
            files = files.Where(f => !touched.Contains(f.Path)).Concat(repaired.Select(c => DiffOne(repo, baseRev, headRev, c)).OfType<FileHunks>()).ToList();
        }
        var byPath = changes.ToDictionary(c => c.Path, StringComparer.Ordinal);
        GitChange? Both(FileHunks f) =>
            byPath.TryGetValue(f.Path, out var c) && c.BlobBase is not null && c.BlobHead is not null && TrivialEdits.Applies(c.Path) ? c : null;
        var texts = ReadBlobs(repo, files.Select(Both).Where(c => c is not null).SelectMany(c => new[] { c!.BlobBase!, c.BlobHead! }).Distinct().ToList());
        return files.Select(f =>
            Both(f) is { } c && texts.TryGetValue(c.BlobBase!, out var before) && texts.TryGetValue(c.BlobHead!, out var after)
                ? TrivialEdits.Mark(f, before, after) : f).ToList();
    }

    /// <summary>
    /// Returns the hunks of one change on its own, keyed by its path, or null when it has none. A rename is diffed
    /// between its two blobs, and an addition or a removal between the two revisions without rename detection.
    /// </summary>
    private static FileHunks? DiffOne(string repo, string baseRev, string headRev, GitChange change)
    {
        if (change.Status == "renamed" && change.BlobBase == change.BlobHead) return null;
        string[] args = change.Status == "renamed"
            ? ["-c", "core.quotePath=false", "diff", "--no-color", "--no-ext-diff", "--unified=3", "--src-prefix=a/", "--dst-prefix=b/", change.BlobBase!, change.BlobHead!]
            : ["-c", "core.quotePath=false", "diff", "--no-color", "--no-ext-diff", "--no-renames", "--relative", "--unified=3", "--src-prefix=a/", "--dst-prefix=b/", baseRev, headRev, "--", change.Path];
        return Parse(Git.RunExact(repo, args)).FirstOrDefault() is { } file ? file with { Path = change.Path } : null;
    }

    /// <summary>
    /// Returns the contents of objects, keyed by the names given, read in one <c>git cat-file --batch</c> process. A
    /// name may be a blob identifier or a <c>revision:path</c> name, and a name that does not exist is left out.
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
        // The newline that ends the text ends its last line, and is not an empty line of its own.
        if (text.EndsWith('\n')) text = text[..^1];
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
                // Git ends a path that holds a space with a tab, for the sake of patch.
                var path = line[4..].TrimEnd('\t');
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
