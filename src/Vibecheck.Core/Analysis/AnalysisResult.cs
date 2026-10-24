using System.Text.Json.Serialization;

namespace Vibecheck.Analysis;

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

    /// <summary>Gets every type declared in source and every member those types declare, one entry for each syntax that declares it.</summary>
    public IReadOnlyList<Declaration> Declarations { get; init; } = [];

    /// <summary>
    /// Gets the references to declarations in the solution that <see cref="Dependencies"/> does not record: those from a
    /// type to its own members, and those that name a constructor. Readers take the union of the two lists.
    /// </summary>
    public IReadOnlyList<Site> Sites { get; init; } = [];
}

/// <summary>
/// A type or member declared in source, as one syntax declares it. See <c>docs/attention.md</c> section 3.
/// </summary>
/// <param name="Symbol">The name <see cref="RawDependency.Symbol"/> uses: the containing type and the member's name, without parameters, so that overloads share it.</param>
/// <param name="Type">The top-level type that holds the declaration, named as <see cref="TypeInfo.Name"/> names it.</param>
/// <param name="Container">The symbol of the type that directly contains the declaration, or <see langword="null"/> for a top-level type.</param>
/// <param name="Kind">One of <c>type</c>, <c>method</c>, <c>constructor</c>, <c>property</c>, <c>field</c>, <c>event</c>, <c>indexer</c> or <c>operator</c>.</param>
/// <param name="Accessibility">The declared accessibility as C# writes it, such as <c>public</c> or <c>protected internal</c>.</param>
/// <param name="Exposed">Whether code outside the assembly can see the declaration, because it and every type that contains it are public or protected.</param>
/// <param name="File">The file that holds the syntax.</param>
/// <param name="FirstLine">The first line of the syntax, one-based, including its attributes but not its documentation comment.</param>
/// <param name="LastLine">The last line of the syntax, one-based.</param>
/// <param name="BodyLine">The line where the body begins, or <see langword="null"/> for a declaration without one. A type's body begins at its opening brace.</param>
/// <param name="Shape">A hash of the declaration's tokens outside its body and its name. Two declarations that differ only by name have the same shape.</param>
/// <param name="Body">A hash of the body's tokens, or <see langword="null"/> for a declaration without a body.</param>
/// <param name="Summary">The first sentence of the documentation summary, or <see langword="null"/>.</param>
public sealed record Declaration(string Symbol, string Type, string? Container, string Kind, string Accessibility, bool Exposed, string File, int FirstLine, int LastLine, int? BodyLine, string Shape, string? Body, string? Summary = null)
{
    /// <summary>Gets the member's name without its containing type, or the whole symbol for a top-level type.</summary>
    [JsonIgnore]
    public string Name => Container is null ? Symbol : Symbol[(Container.Length + 1)..];
}

/// <summary>A reference to a declaration in the solution: the declaration's symbol, the type that makes the reference, the top-level type that declares the symbol, and where the reference is.</summary>
public sealed record Site(string Symbol, string FromType, string ToType, string File, int Line);

/// <summary>A compiled source document, with a short content hash so that a later diff can tell modified from untouched. A generated document names its generator.</summary>
public sealed record SourceFile(string Path, string Hash, string Project, string? Generator = null);

/// <summary>
/// A top-level type: its fully qualified name, the assembly that declares it, the
/// files that declare its parts, the generator that produced any of them, the leaf
/// its <c>[Vibecheck]</c> attribute names, the name of a nested type that wrongly
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
