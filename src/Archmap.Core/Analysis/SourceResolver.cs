namespace Archmap.Analysis;

/// <summary>
/// Resolves a syntax tree path to the source document it belongs to, or to the reason
/// it is not one. Identity comes from the solution's documents, not from the path: a
/// generator's output is a source document whether or not the project writes it to
/// disk, and a file under <c>obj</c> is build output even when a project compiles it.
/// </summary>
internal sealed class SourceResolver
{
    private readonly string repoRoot;
    private readonly Dictionary<string, (string Path, string? Generator)> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> rejected = new(StringComparer.OrdinalIgnoreCase);

    public SourceResolver(string repoRoot)
    {
        this.repoRoot = repoRoot;
    }

    /// <summary>Registers a regular document. Returns its relative path when it is a source document, and <see langword="null"/> when it is build output or lies outside the repository.</summary>
    public string? AddDocument(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var full = Full(path);
        if (IsSource(full, out var relative))
        {
            documents[full] = (relative, null);
            return relative;
        }
        rejected[full] = full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
            ? "build output under " + Directory(Relative(full))
            : "outside the repository";
        return null;
    }

    /// <summary>
    /// Registers a source-generated document and returns the path it is known by: its
    /// repository-relative path when it is on disk inside the repository, and otherwise
    /// the project directory, <c>.generated</c>, and the generator's own path.
    /// </summary>
    public string AddGenerated(string? path, string generator, string projectDirectory)
    {
        var raw = string.IsNullOrEmpty(path) ? generator + "/unnamed.g.cs" : path;
        var full = Full(raw);
        var known = full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(raw)
            ? Relative(full)
            : (projectDirectory.Length == 0 ? "" : projectDirectory + "/") + ".generated/" + raw.Replace('\\', '/').TrimStart('/');
        documents[full] = (known, generator);
        return known;
    }

    /// <summary>Returns the known path of a source document, generated or not, or <see langword="null"/>.</summary>
    public string? SourceOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return documents.TryGetValue(Full(path), out var d) ? d.Path : null;
    }

    /// <summary>Returns the generator that produced a document, or <see langword="null"/> for a regular document or an unknown path.</summary>
    public string? GeneratorOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return documents.TryGetValue(Full(path), out var d) ? d.Generator : null;
    }

    /// <summary>Returns the reason a path is not a source document: the build output location, or that it lies outside the repository.</summary>
    public string ReasonFor(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "unknown";
        if (rejected.TryGetValue(Full(path), out var reason)) return reason;
        return Path.IsPathRooted(path) ? "outside the repository" : "unknown: " + path.Replace('\\', '/');
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
