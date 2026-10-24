using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Archmap.Analysis;

/// <summary>
/// Opens a solution with Roslyn and collects every top-level type it declares and
/// every observed dependency between those types. See <c>docs/archmap-spec.md</c>
/// section 3 for what counts as a dependency and how a reference is resolved.
/// </summary>
public static class RoslynAnalyzer
{
    /// <summary>The full metadata name of the attribute a type uses to name its leaf.</summary>
    public const string AttributeName = "Archmap.ArchmapAttribute";

    /// <summary>Formats a type as namespace and name without generic arity, which is the name patterns match.</summary>
    internal static readonly SymbolDisplayFormat TypeNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

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
        var documents = new Dictionary<ProjectId, List<(Document Document, string Path, string? Generator)>>();
        foreach (var project in projects)
        {
            progress?.Report($"compiling {project.Name}");
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) { warnings.Add($"{project.Name}: no compilation"); continue; }
            compilations[project.Id] = compilation;
            var projectDir = ProjectDirectory(repoRoot, project);
            var list = documents[project.Id] = [];
            foreach (var document in project.Documents)
            {
                var path = resolver.AddDocument(document.FilePath);
                if (path is not null) list.Add((document, path, null));
            }
            foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(ct))
            {
                var generator = GeneratorNameOf(generated.FilePath, generated.HintName);
                list.Add((generated, resolver.AddGenerated(generated.FilePath, generator, projectDir), generator));
            }
        }

        var files = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        var types = new Dictionary<(string Assembly, string Name), TypeInfo>();
        var walked = new List<(DependencyWalker.Walked Dependency, string FromAssembly, string File)>();
        var externals = new List<ExternalReference>();
        var unresolvable = new List<UnresolvableReference>();
        var projectInfos = new List<ProjectInfo>();

        foreach (var project in projects)
        {
            if (!compilations.TryGetValue(project.Id, out var compilation)) continue;
            progress?.Report($"walking {project.Name}");
            var assembly = compilation.AssemblyName ?? project.AssemblyName;
            projectInfos.Add(new ProjectInfo(project.Name, assembly, ProjectDirectory(repoRoot, project)));

            foreach (var (document, path, generator) in documents[project.Id])
            {
                if (files.ContainsKey(path)) continue;
                var text = await document.GetTextAsync(ct);
                files[path] = new SourceFile(path, HashOf(Encoding.UTF8.GetBytes(text.ToString())), project.Name, generator);
                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;
                var model = compilation.GetSemanticModel(tree);
                var walker = new DependencyWalker(model, compilation, resolver, path);
                walker.Visit(await tree.GetRootAsync(ct));
                walked.AddRange(walker.Dependencies.Select(d => (d, walker.Assembly, path)));
                externals.AddRange(walker.ExternalReferences);
                unresolvable.AddRange(walker.Unresolvable);
            }

            foreach (var type in TopLevelTypes(compilation.SourceModule.GlobalNamespace))
            {
                var declFiles = type.DeclaringSyntaxReferences
                    .Select(r => resolver.SourceOf(r.SyntaxTree.FilePath))
                    .Where(p => p is not null)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                if (declFiles.Count == 0) continue;
                var name = type.ToDisplayString(TypeNameFormat);
                var generatorName = type.DeclaringSyntaxReferences.Select(r => resolver.GeneratorOf(r.SyntaxTree.FilePath)).FirstOrDefault(g => g is not null);
                var key = (assembly, name);
                if (types.ContainsKey(key)) continue;
                types[key] = new TypeInfo(name, assembly, declFiles!, generatorName, AttributeLeaf(type), MisattributedNested(type), DocSummary.FirstSentence(type.GetDocumentationCommentXml(cancellationToken: ct)));
            }
        }

        // A name declared in more than one assembly, such as a generator's helper type
        // emitted into every project, is qualified by assembly so that each keeps its
        // own identity and its own leaf.
        var duplicated = types.Keys.GroupBy(k => k.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var typeList = types.Values.Select(t => t with { Name = TypeNames.Qualify(t.Name, t.Assembly, duplicated) }).ToList();
        var deps = walked.Select(w => new RawDependency(
            TypeNames.Qualify(w.Dependency.FromType, w.FromAssembly, duplicated),
            TypeNames.Qualify(w.Dependency.ToType, w.Dependency.ToAssembly, duplicated),
            w.Dependency.Kind, w.Dependency.Symbol, w.File, w.Dependency.Line)).ToList();
        var fileAssembly = documents.SelectMany(kv => kv.Value.Select(d => (d.Path, Assembly: compilations.TryGetValue(kv.Key, out var c) ? c.AssemblyName ?? "" : "")))
            .GroupBy(x => x.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Assembly, StringComparer.Ordinal);
        var externalList = externals.Select(e => e with { FromType = TypeNames.Qualify(e.FromType, fileAssembly.GetValueOrDefault(e.File, ""), duplicated) }).ToList();
        var unresolvableList = unresolvable.Select(u => u with { FromType = TypeNames.Qualify(u.FromType, fileAssembly.GetValueOrDefault(u.File, ""), duplicated) }).ToList();

        // Every dependency must join two listed types. The walker guarantees this by
        // construction; the check turns a future regression into a warning, not a silent hole.
        var known = typeList.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var dropped = deps.Where(d => !known.Contains(d.FromType) || !known.Contains(d.ToType)).ToList();
        if (dropped.Count > 0)
        {
            var examples = dropped.Select(d => known.Contains(d.FromType) ? d.ToType : d.FromType).Distinct().Take(5);
            warnings.Add($"analysis bug: {dropped.Count} dependencies reference types that are not listed, such as {string.Join(", ", examples)}");
        }

        return new AnalysisResult
        {
            Files = files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            Types = typeList.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Assembly, StringComparer.Ordinal).ToList(),
            Dependencies = deps.OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.ToType, StringComparer.Ordinal).ToList(),
            ExternalReferences = externalList.OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line).ToList(),
            Unresolvable = unresolvableList.OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.Line).ToList(),
            Projects = projectInfos,
            Warnings = warnings,
        };
    }

    /// <summary>Returns the leaf named by the type's <c>[Archmap]</c> attribute, or <see langword="null"/>.</summary>
    private static string? AttributeLeaf(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != AttributeName) continue;
            if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string leaf) return leaf;
            return "";
        }
        return null;
    }

    /// <summary>Returns the name of the first nested type that carries an <c>[Archmap]</c> attribute, which the checker ignores with a warning, or <see langword="null"/>.</summary>
    private static string? MisattributedNested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            if (AttributeLeaf(nested) is not null) return nested.ToDisplayString(TypeNameFormat);
            var deeper = MisattributedNested(nested);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private static string ProjectDirectory(string repoRoot, Project project) =>
        project.FilePath is null ? "" : Path.GetRelativePath(repoRoot, Path.GetDirectoryName(project.FilePath)!).Replace('\\', '/');

    private static IEnumerable<INamedTypeSymbol> TopLevelTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var t in TopLevelTypes(child)) yield return t;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
            }
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

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
}
