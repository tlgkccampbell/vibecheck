namespace Archmap.Diffing;

/// <summary>
/// The changeset document: the difference between two facts documents, with nothing
/// interpreted. See <c>docs/archmap-spec.md</c> section 6.
/// </summary>
public sealed record Changeset
{
    public string? Base { get; init; }
    public string? Head { get; init; }
    public required StructuralDiff Structural { get; init; }
    public required SortedDictionary<string, NodeContent> Content { get; init; }
    public required List<FileChange> Files { get; init; }
    public required List<EdgeChange> Edges { get; init; }
    public required List<FileHunks> Hunks { get; init; }
}

public sealed record StructuralDiff
{
    public required List<NodeStructuralChange> Nodes { get; init; }
    public required List<EdgeStructuralChange> Edges { get; init; }
    public required List<PolicyChange> Policies { get; init; }
}

/// <summary>A node or external added, removed or modified in the map, with the evidence that justifies it. A gratuitous change is one the minimality rules do not justify.</summary>
public sealed record NodeStructuralChange(string Id, string Op, string Kind, string? Detail, List<string> Evidence, bool Gratuitous);

/// <summary>A declared edge added, removed or modified, with reference counts at base and head.</summary>
public sealed record EdgeStructuralChange(string Level, string From, string To, string Op, int BaseRefs, int HeadRefs, string? Detail, List<string> Evidence, bool Gratuitous);

public sealed record PolicyChange(string Level, string From, string To);

/// <summary>File counts under a node at head, and how many of them changed. The status is <c>added</c>, <c>removed</c>, <c>modified</c>, <c>children</c> or <c>unchanged</c>.</summary>
public sealed record NodeContent(int Files, int Added, int Removed, int Modified, string Status);

public sealed record FileChange(string Path, string Status, string? LeafBase, string? LeafHead);

/// <summary>A lifted edge with its reference counts at base and head. Undeclared marks a new edge at an observed level, which no map change approved.</summary>
public sealed record EdgeChange(string Level, string From, string To, int Base, int Head, string Status, bool Undeclared);

public sealed record FileHunks(string Path, List<Hunk> Hunks);

/// <summary>One hunk of a unified diff. Each line is a two-element array of marker (<c>" "</c>, <c>"-"</c> or <c>"+"</c>) and text.</summary>
public sealed record Hunk(string Header, List<string[]> Lines);
