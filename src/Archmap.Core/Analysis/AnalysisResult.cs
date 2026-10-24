using System.Text.Json.Serialization;

namespace Archmap.Analysis;

/// <summary>
/// What the analyzer observes in a solution, before any map is applied. Types are
/// named by namespace and top-level type name without generic arity. Paths are
/// relative to the repository root and use forward slashes. The checker consumes this
/// and tests construct it directly, so nothing here depends on Roslyn.
/// </summary>
public sealed record AnalysisResult
{
    /// <summary>Every compiled source document, generated or not, that is inside the repository and not build output.</summary>
    public required IReadOnlyList<SourceFile> Files { get; init; }

    /// <summary>Every top-level type declared in source, with the files that declare it.</summary>
    public required IReadOnlyList<TypeInfo> Types { get; init; }

    public required IReadOnlyList<RawDependency> Dependencies { get; init; }
    public required IReadOnlyList<ExternalReference> ExternalReferences { get; init; }
    public required IReadOnlyList<UnresolvableReference> Unresolvable { get; init; }
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A compiled source document, with a short content hash so that a later diff can tell modified from untouched. A generated document names its generator.</summary>
public sealed record SourceFile(string Path, string Hash, string Project, string? Generator = null);

/// <summary>
/// A top-level type: its fully qualified name, the assembly that declares it, the
/// files that declare its parts, the generator that produced any of them, the leaf
/// its <c>[Archmap]</c> attribute names, the name of a nested type that wrongly
/// carries the attribute, and the first sentence of its documentation summary. When the same fully qualified name is declared in more than
/// one assembly, the name is prefixed with the assembly and <c>::</c> so that each
/// type keeps its own identity; patterns match the plain name together with the
/// assembly.
/// </summary>
public sealed record TypeInfo(string Name, string Assembly, IReadOnlyList<string> Files, string? Generator = null, string? Attribute = null, string? MisattributedNested = null, string? Summary = null)
{
    /// <summary>The name without any assembly prefix, which is what patterns match.</summary>
    [JsonIgnore]
    public string PlainName => TypeNames.Plain(Name);

    /// <summary>The namespace part of the plain name, or an empty string for the global namespace.</summary>
    [JsonIgnore]
    public string Namespace
    {
        get
        {
            var plain = PlainName;
            var i = plain.LastIndexOf('.');
            return i < 0 ? "" : plain[..i];
        }
    }
}

/// <summary>Helpers for type names that may carry an assembly prefix.</summary>
public static class TypeNames
{
    /// <summary>Returns the name without an <c>Assembly::</c> prefix.</summary>
    public static string Plain(string name)
    {
        var i = name.IndexOf("::", StringComparison.Ordinal);
        return i < 0 ? name : name[(i + 2)..];
    }

    /// <summary>Prefixes a name with its assembly when the name is declared in more than one assembly.</summary>
    public static string Qualify(string name, string assembly, ISet<string> duplicated) =>
        duplicated.Contains(name) ? assembly + "::" + name : name;
}

/// <summary>
/// An observed dependency from one type to another. The kind is one of
/// <c>inherits</c>, <c>implements</c>, <c>constructs</c>, <c>calls</c>, <c>attribute</c>
/// or <c>references</c>. The file and line locate the reference as evidence.
/// </summary>
public sealed record RawDependency(string FromType, string ToType, string Kind, string Symbol, string File, int Line);

/// <summary>A reference from a type to a symbol that has no source in the solution, identified by its assembly.</summary>
public sealed record ExternalReference(string FromType, string Assembly, string Symbol, string File, int Line);

/// <summary>A reference to a symbol whose declaration is build output or lies outside the repository.</summary>
public sealed record UnresolvableReference(string FromType, string Symbol, string Reason, string File, int Line);

/// <summary>A project in the solution: its name, its assembly name, and its directory relative to the repository root.</summary>
public sealed record ProjectInfo(string Name, string AssemblyName, string Directory);
