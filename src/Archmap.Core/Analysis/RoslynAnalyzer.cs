using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Archmap.Analysis;

/// <summary>
/// Opens a solution with Roslyn and collects every observed dependency between its
/// compiled files. See <c>docs/archmap-spec.md</c> section 3 for what counts as a
/// dependency and how a reference is resolved to a file.
/// </summary>
public static class RoslynAnalyzer
{
    private static bool registered;

    /// <summary>
    /// Registers the MSBuild instance of the current SDK. This must run before any
    /// MSBuild type is loaded, which is why the analyzer's entry point is kept apart
    /// from the method that uses the workspace.
    /// </summary>
    public static void EnsureMSBuild()
    {
        if (registered) return;
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        registered = true;
    }

    /// <summary>Analyzes the solution at a path. The repository root is the directory that every reported path is relative to.</summary>
    public static Task<AnalysisResult> AnalyzeAsync(string repoRoot, string solutionPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureMSBuild();
        return AnalyzeCoreAsync(repoRoot, solutionPath, progress, ct);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<AnalysisResult> AnalyzeCoreAsync(string repoRoot, string solutionPath, IProgress<string>? progress, CancellationToken ct)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var warnings = new List<string>();
        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) warnings.Add(e.Diagnostic.Message);
        });
        progress?.Report($"opening {solutionPath}");
        var solution = await workspace.OpenSolutionAsync(Path.GetFullPath(solutionPath), cancellationToken: ct);

        var files = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        var deps = new List<RawDependency>();
        var externals = new List<ExternalReference>();
        var unresolvable = new List<UnresolvableReference>();
        var partials = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var projects = new List<ProjectInfo>();

        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (project.Language != LanguageNames.CSharp) continue;
            progress?.Report($"compiling {project.Name}");
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) { warnings.Add($"{project.Name}: no compilation"); continue; }
            var projectDir = project.FilePath is null ? "" : Relative(repoRoot, Path.GetDirectoryName(project.FilePath)!);
            var projectFiles = new List<string>();

            foreach (var document in project.Documents)
            {
                if (!IsOnDisk(repoRoot, document.FilePath, out var rel)) continue;
                projectFiles.Add(rel);
                if (files.ContainsKey(rel)) continue;
                files[rel] = new SourceFile(rel, HashOf(document.FilePath!), project.Name);

                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;
                var model = compilation.GetSemanticModel(tree);
                var walker = new DependencyWalker(model, compilation, repoRoot, rel);
                walker.Visit(await tree.GetRootAsync(ct));
                deps.AddRange(walker.Dependencies);
                externals.AddRange(walker.ExternalReferences);
                unresolvable.AddRange(walker.Unresolvable);
            }
            projects.Add(new ProjectInfo(project.Name, projectDir, projectFiles.OrderBy(f => f, StringComparer.Ordinal).ToList()));

            foreach (var type in AllTypes(compilation.SourceModule.GlobalNamespace))
            {
                var declFiles = type.DeclaringSyntaxReferences
                    .Select(r => r.SyntaxTree.FilePath)
                    .Where(p => IsOnDisk(repoRoot, p, out _))
                    .Select(p => Relative(repoRoot, p))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (declFiles.Count > 1)
                {
                    var name = type.ToDisplayString();
                    if (!partials.TryGetValue(name, out var set)) partials[name] = set = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var f in declFiles) set.Add(f);
                }
            }
        }

        return new AnalysisResult
        {
            Files = files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            Dependencies = deps.OrderBy(d => d.FromFile, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.ToFile, StringComparer.Ordinal).ToList(),
            ExternalReferences = externals.OrderBy(e => e.FromFile, StringComparer.Ordinal).ThenBy(e => e.Line).ToList(),
            Unresolvable = unresolvable.OrderBy(u => u.FromFile, StringComparer.Ordinal).ThenBy(u => u.Line).ToList(),
            PartialTypes = partials.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new PartialType(kv.Key, kv.Value.ToList())).ToList(),
            Projects = projects,
            Warnings = warnings,
        };
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var t in AllTypes(child)) yield return t;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in NestedTypes(type)) yield return nested;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in NestedTypes(nested)) yield return deeper;
        }
    }

    /// <summary>
    /// Reports whether a syntax tree path is a compiled source file on disk inside the
    /// repository and outside <c>obj</c> and <c>bin</c>, and returns its relative path.
    /// Generated trees have paths that fail this test.
    /// </summary>
    internal static bool IsOnDisk(string repoRoot, string? path, out string relative)
    {
        relative = "";
        if (string.IsNullOrEmpty(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        if (!full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(full)) return false;
        relative = Relative(repoRoot, full);
        if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = relative.Split('/');
        if (segments.Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) || s.Equals("bin", StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    internal static string Relative(string repoRoot, string fullPath) =>
        Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
    }
}
