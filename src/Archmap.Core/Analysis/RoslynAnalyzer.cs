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
        var projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

        // Index every document first, so that a reference into any project resolves by
        // document identity rather than by path.
        var resolver = new SourceResolver(repoRoot);
        var compilations = new Dictionary<ProjectId, Compilation>();
        foreach (var project in projects)
        {
            progress?.Report($"compiling {project.Name}");
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) { warnings.Add($"{project.Name}: no compilation"); continue; }
            compilations[project.Id] = compilation;
            foreach (var document in project.Documents) resolver.AddDocument(document.FilePath);
            foreach (var generatedDocument in await project.GetSourceGeneratedDocumentsAsync(ct))
            {
                resolver.AddGenerated(generatedDocument.FilePath, GeneratorNameOf(generatedDocument.FilePath, generatedDocument.HintName));
            }
        }

        var files = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        var deps = new List<RawDependency>();
        var externals = new List<ExternalReference>();
        var unresolvable = new List<UnresolvableReference>();
        var partials = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var projectInfos = new List<ProjectInfo>();

        foreach (var project in projects)
        {
            if (!compilations.TryGetValue(project.Id, out var compilation)) continue;
            progress?.Report($"walking {project.Name}");
            var projectDir = project.FilePath is null ? "" : Path.GetRelativePath(repoRoot, Path.GetDirectoryName(project.FilePath)!).Replace('\\', '/');
            var projectFiles = new List<string>();

            foreach (var document in project.Documents)
            {
                var rel = resolver.SourceOf(document.FilePath);
                if (rel is null) continue;
                projectFiles.Add(rel);
                if (files.ContainsKey(rel)) continue;
                files[rel] = new SourceFile(rel, HashOf(document.FilePath!), project.Name);

                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;
                var model = compilation.GetSemanticModel(tree);
                var walker = new DependencyWalker(model, compilation, resolver, rel);
                walker.Visit(await tree.GetRootAsync(ct));
                deps.AddRange(walker.Dependencies);
                externals.AddRange(walker.ExternalReferences);
                unresolvable.AddRange(walker.Unresolvable);
            }
            projectInfos.Add(new ProjectInfo(project.Name, projectDir, projectFiles.OrderBy(f => f, StringComparer.Ordinal).ToList()));

            foreach (var type in AllTypes(compilation.SourceModule.GlobalNamespace))
            {
                var declFiles = type.DeclaringSyntaxReferences
                    .Select(r => resolver.SourceOf(r.SyntaxTree.FilePath))
                    .Where(p => p is not null)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (declFiles.Count > 1)
                {
                    var name = type.ToDisplayString();
                    if (!partials.TryGetValue(name, out var set)) partials[name] = set = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var f in declFiles) set.Add(f!);
                }
            }
        }

        // Every dependency must join two listed files. The walker guarantees this by
        // construction; the check turns a future regression into a warning, not a silent hole.
        var dropped = deps.Count(d => !files.ContainsKey(d.FromFile) || !files.ContainsKey(d.ToFile));
        if (dropped > 0) warnings.Add($"analysis bug: {dropped} dependencies reference files that are not listed");

        return new AnalysisResult
        {
            Files = files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            Dependencies = deps.OrderBy(d => d.FromFile, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.ToFile, StringComparer.Ordinal).ToList(),
            ExternalReferences = externals.OrderBy(e => e.FromFile, StringComparer.Ordinal).ThenBy(e => e.Line).ToList(),
            Unresolvable = unresolvable.OrderBy(u => u.FromFile, StringComparer.Ordinal).ThenBy(u => u.Line).ToList(),
            PartialTypes = partials.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new PartialType(kv.Key, kv.Value.ToList())).ToList(),
            Projects = projectInfos,
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
    /// Returns the generator's type name from a generated document's path. Roslyn lays
    /// the path out as the generator's assembly name, then its type name, then the hint
    /// name, whether or not the project writes generated files to disk. The hint name
    /// may itself contain directories, so it is stripped before the last segment is read.
    /// </summary>
    internal static string GeneratorNameOf(string? path, string hintName)
    {
        if (string.IsNullOrEmpty(path)) return "unknown generator";
        var normalized = path.Replace('\\', '/');
        var hint = hintName.Replace('\\', '/');
        if (normalized.EndsWith(hint, StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^hint.Length];
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[^1] : "generator of " + hintName;
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
    }
}
