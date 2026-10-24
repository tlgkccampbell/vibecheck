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

    /// <summary>Every pair of leaves, or leaf and external, with references at base or at head. The viewer aggregates these to the level it draws.</summary>
    public required List<EdgeChange> Edges { get; init; }

    public required List<FileHunks> Hunks { get; init; }
}

public sealed record StructuralDiff
{
    public required List<NodeChange> Nodes { get; init; }
    public required List<ViewChange> Views { get; init; }

    /// <summary>Types that bind to a different leaf at head than at base. Membership is structure, so these are listed here.</summary>
    public required List<TypeMove> Types { get; init; }
}

/// <summary>A node or external added, removed or modified in the map. The kind is <c>node</c> or <c>external</c>, and the detail says what changed on a modification.</summary>
public sealed record NodeChange(string Id, string Op, string Kind, string? Detail);

/// <summary>A view added, removed or modified in the map.</summary>
public sealed record ViewChange(string Id, string Op, string? Detail);

/// <summary>A type whose leaf changed between base and head, and how it is bound at head. A type present at one end only has a null leaf at the other.</summary>
public sealed record TypeMove(string Name, string? LeafBase, string? LeafHead, string? By);

/// <summary>File counts under a node at head, and how many of them changed. The status is <c>added</c>, <c>removed</c>, <c>modified</c>, <c>children</c> or <c>unchanged</c>.</summary>
public sealed record NodeContent(int Files, int Added, int Removed, int Modified, string Status);

/// <summary>A file's change status and the leaves its types bound to at base and at head.</summary>
public sealed record FileChange(string Path, string Status, IReadOnlyList<string> LeavesBase, IReadOnlyList<string> LeavesHead);

/// <summary>A leaf-pair edge with its reference counts at base and head. The status is <c>added</c>, <c>removed</c>, <c>modified</c> or <c>unchanged</c>.</summary>
public sealed record EdgeChange(string From, string To, int Base, int Head, string Status);

public sealed record FileHunks(string Path, List<Hunk> Hunks);

/// <summary>One hunk of a unified diff. Each line is a two-element array of marker (<c>" "</c>, <c>"-"</c> or <c>"+"</c>) and text.</summary>
public sealed record Hunk(string Header, List<string[]> Lines);
