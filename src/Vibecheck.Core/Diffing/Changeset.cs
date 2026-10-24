using System.Text.RegularExpressions;
using Vibecheck.Checking;
using Vibecheck.Model;

namespace Vibecheck.Diffing;

/// <summary>
/// The changeset document: the difference between two facts documents, with nothing
/// interpreted. It carries everything the viewer draws, so that the viewer reads
/// neither the code nor the map. See <c>docs/vibecheck-spec.md</c> section 6.
/// </summary>
public sealed record Changeset
{
    public string? Base { get; init; }
    public string? Head { get; init; }

    /// <summary>The map at head. The viewer takes the tree, the views and the externals from here.</summary>
    public required ArchitectureMap Map { get; init; }

    public required StructuralDiff Structural { get; init; }
    public required SortedDictionary<string, NodeContent> Content { get; init; }
    public required List<FileChange> Files { get; init; }

    /// <summary>Every type at either end, with the leaf it binds to and the files that declare it, so that the viewer can descend from a leaf to its types to their files.</summary>
    public required List<TypeChange> Types { get; init; }

    /// <summary>Every pair of leaves, or leaf and external, with references at base or at head. The viewer aggregates these to the level it draws.</summary>
    public required List<EdgeChange> Edges { get; init; }

    /// <summary>Metrics per node at base and at head, including <see cref="ArchitectureMap.Unsorted"/> when either end holds anything.</summary>
    public required SortedDictionary<string, MetricsChange> Metrics { get; init; }

    /// <summary>The edges among each level's children and the cycles among them at head, keyed by the level.</summary>
    public required SortedDictionary<string, LevelMetrics> Levels { get; init; }

    /// <summary>The same metrics for each view's groups at head, keyed by the view.</summary>
    public required SortedDictionary<string, ViewMetrics> Views { get; init; }

    /// <summary>The hunks of every changed file, including those that no type binds, keyed by the file's path.</summary>
    public required List<FileHunks> Hunks { get; init; }

    /// <summary>Gets the hunks that change a declaration, each with the hunks that follow from it, as <see cref="Clustering"/> finds them.</summary>
    public List<Decision> Decisions { get; init; } = [];

    /// <summary>Gets the order in which to read the changeset, as <see cref="ReadingPath"/> plans it.</summary>
    public List<PathStep> Path { get; init; } = [];
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
/// <c>modified</c>, <c>children</c> or <c>unchanged</c>. A renamed file counts as modified under a node that holds one of
/// its leaves at both ends, as removed under one that holds only its leaves at base, and as added under one that holds
/// only its leaves at head.
/// </summary>
public sealed record NodeContent(int Files, int Added, int Removed, int Modified, string Status);

/// <summary>
/// A file's change status and the leaves its types bound to at base and at head. The status is <c>added</c>,
/// <c>removed</c>, <c>modified</c>, <c>renamed</c> or <c>unchanged</c>. The path is the file's path at head, or at base
/// for a removed file, and a renamed file also has its path at base. A changed file that no type binds has no leaves at
/// either end. The blobs are git's object identifiers of the file at each end, when git reports the file as changed.
/// </summary>
public sealed record FileChange(string Path, string Status, IReadOnlyList<string> LeavesBase, IReadOnlyList<string> LeavesHead)
{
    /// <summary>Gets the path of a renamed file at base, or <see langword="null"/> for any other file.</summary>
    public string? PreviousPath { get; init; }

    /// <summary>Gets git's object identifier of the file at base, or <see langword="null"/> when git does not report it or the file is new.</summary>
    public string? BlobBase { get; init; }

    /// <summary>Gets git's object identifier of the file at head, or <see langword="null"/> when git does not report it or the file is gone.</summary>
    public string? BlobHead { get; init; }
}

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

/// <summary>
/// The hunks of one file. Trivial is true when every edit to the file is trivial, as <see cref="TrivialEdits"/> defines
/// it, and null otherwise. The reason says why the tool sets the file aside: the kind of trivial edit its hunks hold,
/// or <see cref="EditReason.Generated"/> for a file a generator wrote, which is set aside whether or not its edits are
/// trivial. It is null for a file the tool does not set aside.
/// </summary>
public sealed record FileHunks(string Path, List<Hunk> Hunks, bool? Trivial = null)
{
    /// <summary>Gets why the tool sets the file aside, or <see langword="null"/> when it does not.</summary>
    public EditReason? Reason { get; init; }
}

/// <summary>
/// One hunk of a unified diff. Each line is a two-element array of marker (<c>" "</c>, <c>"-"</c> or <c>"+"</c>) and
/// text. Trivial is true when the hunk's edit is trivial, as <see cref="TrivialEdits"/> defines it, and null otherwise.
/// </summary>
public sealed partial record Hunk(string Header, List<string[]> Lines, bool? Trivial = null)
{
    /// <summary>Gets the kind of trivial edit the hunk holds, or <see langword="null"/> when the hunk is not trivial.</summary>
    public EditReason? Reason { get; init; }

    /// <summary>
    /// Gets the identifiers of the decisions this hunk is a consequence of, the one with the most sites in the hunk
    /// first, or <see langword="null"/> when the hunk is not a consequence. See <see cref="Clustering"/>.
    /// </summary>
    public List<string>? Follows { get; init; }

    /// <summary>Reads the base and head line ranges of the hunk's header, such as <c>@@ -12,7 +12,9 @@</c>, as zero-based first lines and counts.</summary>
    public bool TryRanges(out (int Start, int Count) old, out (int Start, int Count) now)
    {
        old = now = default;
        var m = HeaderPattern().Match(Header);
        if (!m.Success) return false;
        static (int, int) Range(Group start, Group count) => (int.Parse(start.Value) - 1, count.Success ? int.Parse(count.Value) : 1);
        old = Range(m.Groups[1], m.Groups[2]);
        now = Range(m.Groups[3], m.Groups[4]);
        return true;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HeaderPattern();
}

/// <summary>
/// A hunk that holds a declaration added, removed, reshaped, renamed or moved, with the hunks that follow from it. The
/// identifier is the file and the hunk's index joined by <c>#</c>. A decision is public when any of its declarations can
/// be seen outside its assembly, where callers may exist that the changeset cannot show. See
/// <c>docs/attention.md</c> section 4.
/// </summary>
public sealed record Decision(string Id, string File, int Hunk, bool Public, List<DecisionDeclaration> Declarations, List<Consequence> Consequences)
{
    /// <summary>
    /// Gets the files whose every hunk is trivial or a consequence of this decision alone, which the decision's mark
    /// action marks reviewed. A file with any other edit, including a line that only another decision explains, is
    /// left out.
    /// </summary>
    public List<string> Marks { get; init; } = [];
}

/// <summary>
/// A declaration that a decision changes: its symbol at head, or at base when it was removed, how it changed as
/// <see cref="DeclarationChange"/> says, its kind and accessibility, the number of sites that reference it at each end,
/// and its symbol at base when it was renamed or moved.
/// </summary>
public sealed record DecisionDeclaration(string Symbol, string Change, string Kind, string Accessibility, int SitesBase, int SitesHead, string? Previous = null);

/// <summary>A hunk that follows from a decision, by its file and its index, with the number of its changed lines that reference the decision's declarations.</summary>
public sealed record Consequence(string File, int Hunk, int Lines);

/// <summary>
/// Why an edit cannot change what the program does, or why the tool sets a file aside, as <c>docs/attention.md</c>
/// section 5 describes. The kind is one of the constants of this record, and the other members carry what the kind
/// mentions: the renamed declaration and its previous symbol for a rename consequence, with the identifier of the
/// decision that renamed it once the decisions are known; the file at the other end for a moved declaration.
/// </summary>
public sealed record EditReason(string Kind, string? Symbol = null, string? Previous = null, string? Path = null, string? Decision = null)
{
    /// <summary>The edit touches only comments, whitespace, line breaks, regions and pragmas, so no code changed.</summary>
    public const string Trivial = "trivial";

    /// <summary>Every changed line differs from its pair on the other side only by a renamed declaration's name.</summary>
    public const string Rename = "rename";

    /// <summary>The changed lines are declarations moved, unchanged, between files of one type.</summary>
    public const string Moved = "moved";

    /// <summary>The changed lines are declarations reordered, unchanged, within their file.</summary>
    public const string Reordered = "reordered";

    /// <summary>The changed lines are <c>using</c> directives alone.</summary>
    public const string Usings = "usings";

    /// <summary>A generator wrote the file, which an <c>auto-generated</c> header or a <c>.Designer.cs</c> or <c>.g.cs</c> name says.</summary>
    public const string Generated = "generated";
}
