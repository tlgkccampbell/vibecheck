namespace Archmap.Analysis;

/// <summary>
/// What the analyzer observes in a solution, before any map is applied. Paths are
/// relative to the repository root and use forward slashes. The checker consumes this
/// and tests construct it directly, so nothing here depends on Roslyn.
/// </summary>
public sealed record AnalysisResult
{
    public required IReadOnlyList<SourceFile> Files { get; init; }
    public required IReadOnlyList<RawDependency> Dependencies { get; init; }
    public required IReadOnlyList<ExternalReference> ExternalReferences { get; init; }
    public required IReadOnlyList<UnresolvableReference> Unresolvable { get; init; }
    public required IReadOnlyList<PartialType> PartialTypes { get; init; }
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A compiled source file on disk, with a short content hash so that a later diff can tell modified from untouched.</summary>
public sealed record SourceFile(string Path, string Hash, string Project);

/// <summary>
/// An observed dependency from one file to another. The kind is one of
/// <c>inherits</c>, <c>implements</c>, <c>constructs</c>, <c>calls</c>, <c>attribute</c>
/// or <c>references</c>. The type is the simple name of the referenced type, or of the
/// type that contains the referenced member.
/// </summary>
public sealed record RawDependency(string FromFile, string ToFile, string Kind, string Symbol, string Type, int Line);

/// <summary>A reference from a file to a symbol that has no source in the solution, identified by its assembly.</summary>
public sealed record ExternalReference(string FromFile, string Assembly, string Symbol, int Line);

/// <summary>A reference to a symbol whose declaration is a generated syntax tree with no file on disk.</summary>
public sealed record UnresolvableReference(string FromFile, string Symbol, string Generator, int Line);

/// <summary>A type declared in more than one file.</summary>
public sealed record PartialType(string Name, IReadOnlyList<string> Files);

/// <summary>A project in the solution: its name, its directory relative to the repository root, and the compiled files it owns.</summary>
public sealed record ProjectInfo(string Name, string Directory, IReadOnlyList<string> Files);
