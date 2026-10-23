namespace Archmap.Analysis;

/// <summary>
/// Resolves a syntax tree path to the compiled source file it belongs to, or to the
/// reason it is not one. Identity comes from the solution's documents, not from the
/// path: a generator's output is generated even when a project writes it to disk, and a
/// file under <c>obj</c> is build output even when a project compiles it.
/// </summary>
internal sealed class SourceResolver
{
    private readonly string repoRoot;
    private readonly Dictionary<string, string> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> generated = new(StringComparer.OrdinalIgnoreCase);

    public SourceResolver(string repoRoot)
    {
        this.repoRoot = repoRoot;
    }

    /// <summary>The compiled source files, as repository-relative paths in ordinal order.</summary>
    public IEnumerable<string> SourceFiles => documents.Values.OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>Registers a regular document. Returns its relative path when it is a compiled source file, and <see langword="null"/> when it is build output or lies outside the repository.</summary>
    public string? AddDocument(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var full = Full(path);
        if (IsSource(full, out var relative))
        {
            documents[full] = relative;
            return relative;
        }
        generated[full] = full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
            ? "build output under " + Directory(Relative(full))
            : "outside the repository";
        return null;
    }

    /// <summary>Registers a source-generated document with the name of the generator that produced it.</summary>
    public void AddGenerated(string? path, string generator)
    {
        if (string.IsNullOrEmpty(path)) return;
        generated[Full(path)] = generator;
    }

    /// <summary>Returns the relative path of a compiled source file, or <see langword="null"/>.</summary>
    public string? SourceOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return documents.GetValueOrDefault(Full(path));
    }

    /// <summary>Returns the reason a path is not a compiled source file: the generator name, the build output location, or the path's first segment for a generator hint name.</summary>
    public string ReasonFor(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "unknown";
        if (generated.TryGetValue(Full(path), out var reason)) return reason;
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(path)) return "outside the repository";
        var i = normalized.IndexOf('/');
        return i < 0 ? normalized : normalized[..i];
    }

    private bool IsSource(string full, out string relative)
    {
        relative = "";
        if (!full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)) return false;
        relative = Relative(full);
        if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
        return !relative.Split('/').Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) || s.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    private string Relative(string full) => Path.GetRelativePath(repoRoot, full).Replace('\\', '/');

    private static string Directory(string relative)
    {
        var i = relative.LastIndexOf('/');
        return i < 0 ? "." : relative[..i];
    }
}
