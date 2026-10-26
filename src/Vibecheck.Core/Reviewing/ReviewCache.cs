using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Vibecheck.Analysis;
using Vibecheck.Assist;
using Vibecheck.Checking;
using Vibecheck.Diffing;

namespace Vibecheck.Reviewing;

/// <summary>
/// Identifies what is analyzed at a commit: the directory, relative to the repository's top, that the analysis's paths
/// are relative to, the solution relative to that directory, and how the solution is prepared first.
/// </summary>
public sealed record AnalysisTarget(string Root, string Solution, SolutionPreparation Preparation);

/// <summary>
/// Represents the cache of one repository's analyses and changesets, kept outside the repository so that a second look
/// at a range, or at a range that shares a commit with one seen before, costs no compilation. Every cache of every
/// repository shares one size limit. See <c>docs/review-workflow.md</c> section 5.
/// </summary>
public sealed class ReviewCache : INarrativeCache, IExplainerCache
{
    /// <summary>The size the caches of all repositories may reach together before the least recently used entries are evicted.</summary>
    public const long DefaultLimit = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Identifies the build of the library, so that an entry written by another build is never read. A cached
    /// analysis or changeset is only as good as the code that produced it.
    /// </summary>
    private static readonly string ToolId = typeof(ReviewCache).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    private readonly ConcurrentDictionary<string, Lazy<Task<AnalysisResult>>> running = new(StringComparer.Ordinal);
    private readonly string top;
    private readonly long limit;

    /// <summary>Opens the cache in a directory, which is created on the first write, under a root shared with other repositories' caches.</summary>
    public ReviewCache(string root, string directory, long limit = DefaultLimit)
    {
        top = Path.GetFullPath(root);
        Directory = Path.GetFullPath(directory);
        this.limit = limit;
    }

    /// <summary>Gets the directory that holds this repository's entries.</summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the file that holds the repository's reviewed marks, which <see cref="ReviewedFiles"/> reads and writes. It
    /// is never evicted, since a mark is the reviewer's own record and not something the tool can produce again.
    /// </summary>
    public string ReviewedPath => Path.Combine(Directory, "reviewed.json");

    /// <summary>
    /// Returns the root that every repository's cache lives under: the <c>VIBECHECK_CACHE</c> environment variable when it
    /// is set, the local application data folder on Windows, and the XDG cache directory elsewhere.
    /// </summary>
    public static string DefaultRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("VIBECHECK_CACHE");
        if (!string.IsNullOrEmpty(overridden)) return overridden;
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vibecheck");
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cache = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache") : xdg;
        return Path.Combine(cache, "vibecheck");
    }

    /// <summary>
    /// Opens the cache of the repository that contains a directory. A repository is identified by its first root
    /// commit, so that its clones and worktrees share one cache.
    /// </summary>
    public static ReviewCache ForRepository(string dir, string? root = null)
    {
        root ??= DefaultRoot();
        var roots = Git.Run(dir, "rev-list", "--max-parents=0", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var id = roots.Order(StringComparer.Ordinal).First()[..16];
        return new ReviewCache(root, Path.Combine(root, id));
    }

    /// <summary>
    /// Returns the analysis of a target at a commit from the cache, or produces it, stores it and returns it. Concurrent
    /// requests for the same analysis share one production. A production that fails is not stored.
    /// </summary>
    public async Task<AnalysisResult> AnalysisAsync(AnalysisTarget target, string commit, Func<Task<AnalysisResult>> produce, IProgress<string>? progress = null)
    {
        var path = Path.Combine(Directory, "analyses", $"{commit}-{Hash(target.Root, target.Solution, Key(target.Preparation), ToolId)}.json.gz");
        var lazy = running.GetOrAdd(path, _ => new Lazy<Task<AnalysisResult>>(async () =>
        {
            var cached = Read<AnalysisResult>(path);
            if (cached is not null) { progress?.Report("cached"); return cached; }
            var analysis = await produce();
            Write(path, analysis);
            return analysis;
        }));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            running.TryRemove(new KeyValuePair<string, Lazy<Task<AnalysisResult>>>(path, lazy));
        }
    }

    /// <summary>
    /// Returns the cached changeset between two commits drawn with a map, or <see langword="null"/> when there is none.
    /// The variant names anything else the changeset depends on, such as the renames it pairs files by.
    /// </summary>
    public Changeset? ReadChangeset(string baseCommit, string headCommit, AnalysisTarget target, string mapText, string? layoutText, string variant = "") =>
        Read<Changeset>(ChangesetPath(baseCommit, headCommit, target, mapText, layoutText, variant));

    /// <summary>Stores the changeset between two commits drawn with a map, under a variant as <see cref="ReadChangeset"/> describes.</summary>
    public void WriteChangeset(string baseCommit, string headCommit, AnalysisTarget target, string mapText, string? layoutText, Changeset changeset, string variant = "") =>
        Write(ChangesetPath(baseCommit, headCommit, target, mapText, layoutText, variant), changeset);

    private string ChangesetPath(string baseCommit, string headCommit, AnalysisTarget target, string mapText, string? layoutText, string variant) =>
        Path.Combine(Directory, "changesets", $"{baseCommit[..12]}-{headCommit[..12]}-{Hash(baseCommit, headCommit, target.Root, target.Solution, Key(target.Preparation), mapText, layoutText ?? "", variant, ToolId)}.json.gz");

    /// <summary>
    /// Returns the latest version of the map Claude generated for a solution, and its version number, or
    /// <see langword="null"/> when none has been generated. Generated maps are kept apart from the evicted entries,
    /// since a map is what keeps the picture of a repository stable from one review to the next.
    /// </summary>
    public (string Text, int Version)? ReadGeneratedMap(AnalysisTarget target)
    {
        var dir = GeneratedMapDirectory(target);
        if (!System.IO.Directory.Exists(dir)) return null;
        var latest = System.IO.Directory.EnumerateFiles(dir, "*.json")
            .Select(f => (Path: f, Version: int.TryParse(Path.GetFileNameWithoutExtension(f), out var v) ? v : 0))
            .Where(f => f.Version > 0).OrderByDescending(f => f.Version).FirstOrDefault();
        return latest.Path is null ? null : (File.ReadAllText(latest.Path), latest.Version);
    }

    /// <summary>Stores a new version of the map Claude generated for a solution, and returns its version number. Every version is kept.</summary>
    public int WriteGeneratedMap(AnalysisTarget target, string text)
    {
        lock (running)
        {
            var version = (ReadGeneratedMap(target)?.Version ?? 0) + 1;
            var path = Path.Combine(GeneratedMapDirectory(target), $"{version:D4}.json");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return version;
        }
    }

    /// <summary>Returns the checked narratives stored under a request key, or <see langword="null"/> when there are none.</summary>
    public IReadOnlyList<Narrative>? ReadNarratives(string key) => Read<List<Narrative>>(NarrativePath(key));

    /// <summary>Stores the checked narratives of a request under its key, replacing any stored before.</summary>
    public void WriteNarratives(string key, IReadOnlyList<Narrative> narratives) => Write(NarrativePath(key), narratives.ToList());

    private string NarrativePath(string key) => Path.Combine(Directory, "narratives", key + ".json.gz");

    /// <summary>Returns the checked explainer stored under a request key, or <see langword="null"/> when there is none.</summary>
    public Explainer? ReadExplainer(string key) => Read<Explainer>(ExplainerPath(key));

    /// <summary>Stores the checked explainer of a request under its key, replacing any stored before.</summary>
    public void WriteExplainer(string key, Explainer explainer) => Write(ExplainerPath(key), explainer);

    private string ExplainerPath(string key) => Path.Combine(Directory, "explainers", key + ".json.gz");

    private string GeneratedMapDirectory(AnalysisTarget target) => Path.Combine(Directory, "maps", Hash(target.Root, target.Solution));

    /// <summary>Returns a short hexadecimal hash of strings, each kept distinct from its neighbors.</summary>
    /// <summary>
    /// Returns the part of a cache key that names a preparation. A build and no preparation keep the names they had
    /// when the only choice was whether to build, so that analyses cached then are still found.
    /// </summary>
    private static string Key(SolutionPreparation preparation) => preparation switch
    {
        SolutionPreparation.Build => "build",
        SolutionPreparation.None => "no-build",
        _ => "restore",
    };

    private static string Hash(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))))[..16];

    /// <summary>Reads a compressed JSON entry and marks it as just used, or returns <see langword="null"/> when it is missing or unreadable.</summary>
    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using (var file = File.OpenRead(path))
            using (var zip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(zip, Encoding.UTF8))
            {
                var value = FactsJson.Deserialize<T>(reader.ReadToEnd());
                file.Close();
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return value;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // A damaged entry is treated as missing, and the next write replaces it.
            return null;
        }
    }

    /// <summary>Writes a compressed JSON entry through a temporary file, so that a reader never sees half of one, and then evicts entries over the limit.</summary>
    private void Write<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        using (var file = File.Create(temp))
        using (var zip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new StreamWriter(zip, new UTF8Encoding(false)))
        {
            writer.Write(FactsJson.Serialize(value));
        }
        File.Move(temp, path, overwrite: true);
        Evict();
    }

    /// <summary>
    /// Deletes the least recently used entries of every repository's cache until the total size is within the limit.
    /// Analyses go before changesets, since an analysis is the larger entry and a changeset rebuilds from analyses.
    /// </summary>
    private void Evict()
    {
        if (!System.IO.Directory.Exists(top)) return;
        var files = new DirectoryInfo(top).EnumerateFiles("*.json.gz", SearchOption.AllDirectories).ToList();
        var total = files.Sum(f => f.Length);
        if (total <= limit) return;
        var order = files
            .OrderBy(f => f.Directory?.Name == "analyses" ? 0 : 1)
            .ThenBy(f => f.LastWriteTimeUtc);
        foreach (var f in order)
        {
            if (total <= limit) break;
            try { f.Delete(); total -= f.Length; }
            catch (IOException) { /* An entry being read is left for the next eviction. */ }
        }
    }
}
