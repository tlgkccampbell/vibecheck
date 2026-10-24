using System.Text;
using System.Text.Json;
using Vibecheck.Diffing;

namespace Vibecheck.Reviewing;

/// <summary>
/// Represents the reviewed marks of one repository. A changed file is reviewed when the reviewer has set it aside, or
/// when the tool has set it aside on its own and the reviewer has not unmarked it. A mark belongs to a file's change: it
/// is keyed by the file's path from the repository's top and git's identifiers of the file at base and at head, so a
/// push that changes the file clears the mark, and one that leaves the file alone keeps it. The reviewer's choices are
/// kept in a small file of their own, or only in memory when no file is given. See <c>docs/pull-requests.md</c> section 5.
/// </summary>
public sealed class ReviewedFiles
{
    /// <summary>The reason of a file that the reviewer marked.</summary>
    public const string ByReviewer = "reviewer";

    /// <summary>The reason of a file whose edits are all trivial, as <see cref="TrivialEdits"/> defines them.</summary>
    public const string NoCodeChanged = "trivial";

    /// <summary>The reason of a renamed file whose contents did not change.</summary>
    public const string RenamedWithoutChanges = "renamed";

    private readonly string? path;
    private readonly Dictionary<string, bool> choices;

    /// <summary>Opens the marks stored in a file, which is created on the first mark, or keeps them in memory when the path is <see langword="null"/>.</summary>
    public ReviewedFiles(string? path)
    {
        this.path = path;
        choices = Read(path);
    }

    /// <summary>
    /// Returns the reviewed files of a changeset by path, each with the reason it is reviewed: <see cref="ByReviewer"/>,
    /// <see cref="NoCodeChanged"/> or <see cref="RenamedWithoutChanges"/>. Unchanged files are never reviewed.
    /// </summary>
    /// <param name="changeset">The changeset whose changed files are looked up.</param>
    /// <param name="root">The directory, relative to the repository's top, that the changeset's paths are relative to.</param>
    public SortedDictionary<string, string> Marks(Changeset changeset, string root)
    {
        var hunks = changeset.Hunks.ToDictionary(h => h.Path, StringComparer.Ordinal);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        lock (choices)
        {
            foreach (var file in changeset.Files.Where(f => f.Status != "unchanged"))
            {
                var tool = ToolReason(file, hunks.GetValueOrDefault(file.Path));
                var reviewed = choices.TryGetValue(Key(file, root), out var chosen) ? chosen : tool is not null;
                if (reviewed) result[file.Path] = tool ?? ByReviewer;
            }
        }
        return result;
    }

    /// <summary>
    /// Records the reviewer's choice for changed files of a changeset and stores it. Paths that name no changed file
    /// are ignored. Returns the keys of the files that were set, which identify the same changes in other changesets.
    /// </summary>
    /// <param name="changeset">The changeset whose changed files are marked.</param>
    /// <param name="root">The directory, relative to the repository's top, that the changeset's paths are relative to.</param>
    /// <param name="paths">The paths of the files, as the changeset gives them.</param>
    /// <param name="reviewed">A value indicating whether the files are reviewed.</param>
    public HashSet<string> Set(Changeset changeset, string root, IEnumerable<string> paths, bool reviewed)
    {
        var wanted = paths.ToHashSet(StringComparer.Ordinal);
        var keys = changeset.Files.Where(f => f.Status != "unchanged" && wanted.Contains(f.Path)).Select(f => Key(f, root)).ToHashSet(StringComparer.Ordinal);
        lock (choices)
        {
            foreach (var key in keys) choices[key] = reviewed;
            Write();
        }
        return keys;
    }

    /// <summary>Returns the keys of the changed files of a changeset by path.</summary>
    public static Dictionary<string, string> Keys(Changeset changeset, string root) =>
        changeset.Files.Where(f => f.Status != "unchanged").ToDictionary(f => f.Path, f => Key(f, root), StringComparer.Ordinal);

    /// <summary>
    /// Returns why the tool marks a changed file reviewed on its own, or <see langword="null"/> when it does not: the
    /// file's edits are all trivial, or the file was renamed and its contents did not change.
    /// </summary>
    public static string? ToolReason(FileChange file, FileHunks? hunks)
    {
        if (hunks?.Trivial == true) return NoCodeChanged;
        if (file.Status == "renamed" && file.BlobBase is not null && file.BlobBase == file.BlobHead) return RenamedWithoutChanges;
        return null;
    }

    /// <summary>
    /// Returns the key of a file's change: its path from the repository's top and git's identifiers of the file at each
    /// end. The path is the head path, or the base path for a removed file, as <see cref="FileChange.Path"/> is.
    /// </summary>
    public static string Key(FileChange file, string root)
    {
        var repoPath = root is "." or "" ? file.Path : root.TrimEnd('/') + "/" + file.Path;
        return $"{repoPath} {file.BlobBase ?? "-"}..{file.BlobHead ?? "-"}";
    }

    private static Dictionary<string, bool> Read(string? path)
    {
        if (path is null || !File.Exists(path)) return new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path, Encoding.UTF8));
            return new Dictionary<string, bool>(stored ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file is read as empty, and the next mark replaces it.
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }
    }

    /// <summary>Writes the choices through a temporary file, so that a reader never sees half of them.</summary>
    private void Write()
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new SortedDictionary<string, bool>(choices, StringComparer.Ordinal)), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
