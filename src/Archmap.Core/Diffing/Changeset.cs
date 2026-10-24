using Archmap.Checking;
using Archmap.Model;

namespace Archmap.Diffing;

/// <summary>
/// The changeset document: the difference between two facts documents, with nothing
/// interpreted. It carries everything the viewer draws, so that the viewer reads
/// neither the code nor the map. See <c>docs/archmap-spec.md</c> section 6.
/// </summary>
public sealed record Changeset
{
    public string? Base { get; init; }
    public string? Head { get; init; }

    /// <summary>The map at head. The viewer takes the tree, the views and the externals from here.</summary>
    public required ArchMap Map { get; init; }

    public required StructuralDiff Structural { get; init; }
    public required SortedDictionary<string, NodeContent> Content { get; init; }
    public required List<FileChange> Files { get; init; }

    /// <summary>Every type at either end, with the leaf it binds to and the files that declare it, so that the viewer can descend from a leaf to its types to their files.</summary>
    public required List<TypeChange> Types { get; init; }

    /// <summary>Every pair of leaves, or leaf and external, with references at base or at head. The viewer aggregates these to the level it draws.</summary>
    public required List<EdgeChange> Edges { get; init; }

    /// <summary>Metrics per node at base and at head, including <see cref="ArchMap.Unsorted"/> when either end holds anything.</summary>
    public required SortedDictionary<string, MetricsChange> Metrics { get; init; }

    /// <summary>The edges among each level's children and the cycles among them at head, keyed by the level.</summary>
    public required SortedDictionary<string, LevelMetrics> Levels { get; init; }

    /// <summary>The same metrics for each view's groups at head, keyed by the view.</summary>
    public required SortedDictionary<string, ViewMetrics> Views { get; init; }

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

/// <summary>A type whose leaf changed between base and head, and how it is bound at head.</summary>
public sealed record TypeMove(string Name, string? LeafBase, string? LeafHead, string? By);

/// <summary>
/// A type at either end of the changeset. The status is <c>added</c>, <c>removed</c>,
/// <c>moved</c> or <c>unchanged</c>, and a type present at one end only has a null leaf
/// at the other. The files are those that declare the type at head, or at base when
/// the type was removed.
/// </summary>
public sealed record TypeChange(string Name, string? LeafBase, string? LeafHead, IReadOnlyList<string> Files, string Status);

/// <summary>
/// File counts under a node at head, and how many of them changed. The status is <c>added</c>, <c>removed</c>,
/// <c>modified</c>, <c>children</c> or <c>unchanged</c>. Trivial counts the modified files whose edits are all
/// trivial, as <see cref="TrivialEdits"/> defines it.
/// </summary>
public sealed record NodeContent(int Files, int Added, int Removed, int Modified, string Status, int Trivial = 0);

/// <summary>A file's change status and the leaves its types bound to at base and at head.</summary>
public sealed record FileChange(string Path, string Status, IReadOnlyList<string> LeavesBase, IReadOnlyList<string> LeavesHead);

/// <summary>
/// A leaf-pair edge with its reference counts at base and head. The status is
/// <c>added</c>, <c>removed</c>, <c>modified</c> or <c>unchanged</c>. A changed edge
/// carries the type pairs whose counts changed as evidence; an unchanged edge carries none.
/// </summary>
public sealed record EdgeChange(string From, string To, int Base, int Head, string Status, List<PairChange>? Evidence = null);

/// <summary>A pair of types behind an edge, with the reference counts at base and head. The target of a pair behind an external edge is the assembly.</summary>
public sealed record PairChange(string From, string To, int Base, int Head);

/// <summary>A node's metrics at base and at head. Either is null when the node exists at one end only.</summary>
public sealed record MetricsChange(NodeMetrics? Base, NodeMetrics? Head);

/// <summary>The hunks of one file. Trivial is true when every edit to the file is trivial, as <see cref="TrivialEdits"/> defines it, and null otherwise.</summary>
public sealed record FileHunks(string Path, List<Hunk> Hunks, bool? Trivial = null);

/// <summary>
/// One hunk of a unified diff. Each line is a two-element array of marker (<c>" "</c>, <c>"-"</c> or <c>"+"</c>) and
/// text. Trivial is true when the hunk's edit is trivial, as <see cref="TrivialEdits"/> defines it, and null otherwise.
/// </summary>
public sealed record Hunk(string Header, List<string[]> Lines, bool? Trivial = null);
