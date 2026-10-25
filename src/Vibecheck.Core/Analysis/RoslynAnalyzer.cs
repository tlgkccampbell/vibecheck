using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace Vibecheck.Analysis;

/// <summary>
/// Opens a solution with Roslyn and collects every top-level type it declares and
/// every observed dependency between those types. See <c>docs/vibecheck-spec.md</c>
/// section 3 for what counts as a dependency and how a reference is resolved.
/// </summary>
public static class RoslynAnalyzer
{
    /// <summary>The full metadata name of the attribute a type uses to name its leaf.</summary>
    public const string AttributeName = "Vibecheck.VibecheckAttribute";

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
        var inputs = new List<ProjectTrees>();
        foreach (var project in projects)
        {
            progress?.Report($"compiling {project.Name}");
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) { warnings.Add($"{project.Name}: no compilation"); continue; }
            var projectDir = ProjectDirectory(repoRoot, project);
            var trees = new List<(SyntaxTree Tree, string Path, string? Generator)>();
            foreach (var document in project.Documents)
            {
                var path = resolver.AddDocument(document.FilePath);
                var tree = path is null ? null : await document.GetSyntaxTreeAsync(ct);
                if (tree is not null) trees.Add((tree, path!, null));
            }
            foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(ct))
            {
                var generator = GeneratorNameOf(generated.FilePath, generated.HintName);
                var path = resolver.AddGenerated(generated.FilePath, generator, projectDir);
                if (await generated.GetSyntaxTreeAsync(ct) is { } tree) trees.Add((tree, path, generator));
            }
            inputs.Add(new ProjectTrees(project.Name, projectDir, compilation, trees));
        }
        return Collect(resolver, inputs, warnings, progress, ct);
    }

    /// <summary>A compiled project and its source documents' syntax trees, each with its path and the generator that produced it.</summary>
    internal sealed record ProjectTrees(string Name, string Directory, Compilation Compilation, List<(SyntaxTree Tree, string Path, string? Generator)> Trees);

    /// <summary>
    /// Builds the analysis of compiled projects whose documents a resolver has registered. Every path the analysis
    /// reports comes from the resolver. Tests call this with compilations built in memory.
    /// </summary>
    internal static AnalysisResult Collect(SourceResolver resolver, IReadOnlyList<ProjectTrees> projects, List<string> warnings, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var files = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        var types = new Dictionary<(string Assembly, string Name), TypeInfo>();
        var walked = new List<(DependencyWalker.Walked Dependency, string FromAssembly, string File)>();
        var walkedSites = new List<(DependencyWalker.WalkedSite Site, string FromAssembly, string File)>();
        var declarations = new List<(Declaration Declaration, string Assembly)>();
        var externals = new List<ExternalReference>();
        var unresolvable = new List<UnresolvableReference>();
        var projectInfos = new List<ProjectInfo>();
        var fileAssembly = new Dictionary<string, string>(StringComparer.Ordinal);

        // A file compiled into more than one project is walked once, with the first project that compiles it.
        var work = new List<(ProjectTrees Project, string Assembly, SyntaxTree Tree, string Path, string? Generator)>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var assembly = project.Compilation.AssemblyName ?? project.Name;
            projectInfos.Add(new ProjectInfo(project.Name, assembly, project.Directory));
            foreach (var (tree, path, generator) in project.Trees)
            {
                fileAssembly.TryAdd(path, assembly);
                if (claimed.Add(path)) work.Add((project, assembly, tree, path, generator));
            }
        }

        // Binding dominates the walk and each file binds independently, so the files are walked in parallel, the
        // largest first so that no long file starts last. The results are merged in their original order.
        progress?.Report($"walking {work.Count} files in {projects.Count} projects");
        var results = new FileWalk[work.Count];
        var largestFirst = Enumerable.Range(0, work.Count).OrderByDescending(i => work[i].Tree.Length).ToList();
        Parallel.ForEach(Partitioner.Create(largestFirst, EnumerablePartitionerOptions.NoBuffering), new ParallelOptions { CancellationToken = ct }, i =>
        {
            var (project, _, tree, path, generator) = work[i];
            results[i] = WalkFile(project, tree, path, generator, resolver, ct);
        });
        for (var i = 0; i < work.Count; i++)
        {
            var (_, assembly, _, path, _) = work[i];
            var (file, walker, declared) = results[i];
            files[path] = file;
            walked.AddRange(walker.Dependencies.Select(d => (d, walker.Assembly, path)));
            walkedSites.AddRange(walker.Sites.Select(s => (s, walker.Assembly, path)));
            externals.AddRange(walker.ExternalReferences);
            unresolvable.AddRange(walker.Unresolvable);
            declarations.AddRange(declared.Declarations.Select(d => (d, assembly)));
        }

        foreach (var project in projects)
        {
            var compilation = project.Compilation;
            var assembly = compilation.AssemblyName ?? project.Name;
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
                types[key] = new TypeInfo(name, assembly, declFiles!, generatorName, AttributeLeaf(type), MisattributedNested(type), DocSummary.FirstSentence(type.GetDocumentationCommentXml(cancellationToken: ct)),
                    TraitsOf(type, compilation.ObjectType.ContainingAssembly));
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
        var sites = walkedSites.Select(w => new Site(w.Site.Symbol,
            TypeNames.Qualify(w.Site.FromType, w.FromAssembly, duplicated),
            TypeNames.Qualify(w.Site.ToType, w.Site.ToAssembly, duplicated), w.File, w.Site.Line)).ToList();
        var declarationList = declarations.Select(d => d.Declaration with { Type = TypeNames.Qualify(d.Declaration.Type, d.Assembly, duplicated) }).ToList();
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
            Declarations = declarationList.OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.FirstLine).ThenBy(d => d.Symbol, StringComparer.Ordinal).ToList(),
            Sites = sites.OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ThenBy(s => s.Symbol, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>A walked source file: its record, the references found in it, and the declarations it makes.</summary>
    private sealed record FileWalk(SourceFile File, DependencyWalker Dependencies, DeclarationWalker Declarations);

    /// <summary>
    /// Walks one source file for its references and declarations. Nullable analysis is disabled because the walkers
    /// read symbols through their original definitions, which carry no nullable annotations, and the analysis costs
    /// about half of the binding time.
    /// </summary>
    private static FileWalk WalkFile(ProjectTrees project, SyntaxTree tree, string path, string? generator, SourceResolver resolver, CancellationToken ct)
    {
        var compilation = project.Compilation;
        var text = tree.GetText(ct);
        var file = new SourceFile(path, HashOf(Encoding.UTF8.GetBytes(text.ToString())), project.Name, generator);
        // The option is marked experimental. Should it be removed, dropping it costs speed, not correctness.
#pragma warning disable RSEXPERIMENTAL001
        var model = compilation.GetSemanticModel(tree, SemanticModelOptions.DisableNullableAnalysis);
#pragma warning restore RSEXPERIMENTAL001
        var root = tree.GetRoot(ct);
        var walker = new DependencyWalker(model, compilation, resolver, path);
        walker.Visit(root);
        var declared = new DeclarationWalker(model, path);
        declared.Visit(root);
        return new FileWalk(file, walker, declared);
    }

    /// <summary>Returns the open form of a type, as <see cref="TraitType"/> describes it.</summary>
    internal static string OpenName(INamedTypeSymbol type)
    {
        static string Generic(INamedTypeSymbol t) => t.Arity == 0 ? t.Name : t.Name + "<" + new string(',', t.Arity - 1) + ">";
        type = type.OriginalDefinition;
        var name = Generic(type);
        for (var c = type.ContainingType; c is not null; c = c.ContainingType) name = Generic(c) + "." + name;
        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
    }

    /// <summary>
    /// Returns the traits of a top-level type, as <see cref="TypeTraits"/> defines them, or <see langword="null"/> when it
    /// has none. A type that does not resolve is not a trait.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="coreLibrary">The assembly that declares <see cref="object"/>, whose interfaces and injected types are not traits.</param>
    internal static TypeTraits? TraitsOf(INamedTypeSymbol type, IAssemblySymbol coreLibrary)
    {
        static bool Resolved(ITypeSymbol? t) => t is INamedTypeSymbol { TypeKind: not TypeKind.Error };
        bool InCore(ISymbol s) => SymbolEqualityComparer.Default.Equals(s.ContainingAssembly, coreLibrary);
        static TraitType Trait(INamedTypeSymbol t) =>
            new(OpenName(t), t.IsGenericType ? t.TypeArguments.Select(a => a.ToDisplayString(DependencyWalker.SymbolFormat)).ToList() : null);

        var baseType = type.TypeKind == TypeKind.Class && Resolved(type.BaseType) && type.BaseType!.SpecialType != SpecialType.System_Object ? Trait(type.BaseType) : null;
        var interfaces = type.Interfaces.Where(i => Resolved(i) && !InCore(i)).Select(Trait).ToList();
        var attributes = type.GetAttributes().Select(a => a.AttributeClass)
            .Where(a => Resolved(a) && a!.ToDisplayString() != AttributeName).Select(a => OpenName(a!)).Distinct(StringComparer.Ordinal).ToList();
        // A primary constructor is the one every other constructor calls, so it says what the type needs.
        var constructor = type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared)
            .OrderByDescending(c => c.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax)).ThenByDescending(c => c.Parameters.Length).FirstOrDefault();
        var injects = constructor is null ? [] : constructor.Parameters.Select(p => p.Type).OfType<INamedTypeSymbol>()
            .Where(t => Resolved(t) && !InCore(t)).Select(OpenName).Distinct(StringComparer.Ordinal).ToList();
        return baseType is null && interfaces.Count == 0 && attributes.Count == 0 && injects.Count == 0 ? null : new TypeTraits(baseType, interfaces, attributes, injects);
    }

    /// <summary>Returns the leaf named by the type's <c>[Vibecheck]</c> attribute, or <see langword="null"/>.</summary>
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

    /// <summary>Returns the name of the first nested type that carries an <c>[Vibecheck]</c> attribute, which the checker ignores with a warning, or <see langword="null"/>.</summary>
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
