using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Vibecheck.Analysis;
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

    /// <summary>Gets the names the changeset introduces, grouped by block and ordered by use, as <see cref="Diffing.Glossary"/> lists them.</summary>
    public List<Term> Glossary { get; init; } = [];

    /// <summary>Gets the test types with a changed file, ordered by name, as <see cref="TestChanges"/> lists them.</summary>
    public List<TestTypeChange> Tests { get; init; } = [];

    /// <summary>Gets the patterns found at base that have an added or changed instance, as <see cref="PatternChanges"/> lists them.</summary>
    public List<PatternChange> Patterns { get; init; } = [];
}

/// <summary>
/// A pattern found at base, as <see cref="PatternFact"/> describes it, with the instances at head that the change added
/// or changed. See <c>docs/patterns.md</c> section 5.7.
/// </summary>
/// <param name="Id">The pattern's identifier.</param>
/// <param name="Name">The pattern's name.</param>
/// <param name="Purpose">The pattern's purpose, or <see langword="null"/>.</param>
/// <param name="By">What named the pattern, as <see cref="PatternFact.By"/> says.</param>
/// <param name="Keys">The keys every instance carries.</param>
/// <param name="Count">The number of instances at base.</param>
/// <param name="Instances">The added and changed instances at head that the pattern reports, ordered by name.</param>
/// <param name="Others">Every other instance at head, ordered by name.</param>
public sealed record PatternChange(string Id, string Name, string? Purpose, string By, IReadOnlyList<string> Keys, int Count, List<PatternInstance> Instances, List<PatternOther> Others)
{
    /// <summary>Gets the pattern's norms at base, most shared first.</summary>
    public List<NormChange> Norms { get; init; } = [];

    /// <summary>Gets the source at head of each instance the pattern reports and of each of their examples, by the type's name.</summary>
    public SortedDictionary<string, TypeSource> Sources { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The source of a type at head, from the file that declares most of it: the lines from its declaration's first line,
/// at most <see cref="PatternExamples.MostLines"/> of them. A longer type is cut at its last whole member within the
/// limit, and is marked as cut.
/// </summary>
public sealed record TypeSource(string File, int First, List<string> Lines, bool Cut);

/// <summary>
/// A row of the compare view, as <see cref="PatternExamples.Align"/> aligns two types: a member of the instance on the
/// left and a member of the other type on the right, each with its name and its lines in the file of its
/// <see cref="TypeSource"/>, either side empty when the member has no counterpart. The marks are the words for the
/// instance's departures and unusual traits that the row shows.
/// </summary>
public sealed record CompareRow(string? Left, LineRange? LeftLines, string? Right, LineRange? RightLines)
{
    /// <summary>Gets the words for the departures and unusual traits the row shows, or <see langword="null"/> when it shows none.</summary>
    public List<string>? Marks { get; init; }
}

/// <summary>The examples of an instance, as <see cref="PatternExamples.Choose"/> chooses them, either absent when the pattern has no other instance to give.</summary>
public sealed record ExampleChoice(string? Nearest, string? Typical);

/// <summary>
/// A norm of a pattern, as <see cref="NormFact"/> describes it, with the words that say an instance has it and the
/// words that say an instance lacks it, as <see cref="PatternFeatures.Text"/> and <see cref="PatternFeatures.Absence"/>
/// give them.
/// </summary>
public sealed record NormChange(string Feature, int Share, string Text, string Absence);

/// <summary>
/// An instance of a pattern at head that the change added or changed. The status is <c>added</c> or <c>changed</c>.
/// See <c>docs/patterns.md</c> sections 5.3 to 5.5.
/// </summary>
public sealed record PatternInstance(string Type, string Status)
{
    /// <summary>
    /// Gets the norms the change is responsible for the instance lacking: every norm an added instance lacks, and every
    /// norm a changed instance had at base and lacks at head, in the order of the pattern's norms.
    /// </summary>
    public List<string> Departures { get; init; } = [];

    /// <summary>Gets every norm the instance lacks at head, including those it lacked before the change, in the order of the pattern's norms.</summary>
    public List<string> Lacks { get; init; } = [];

    /// <summary>Gets the traits of the instance, as keys, that no other instance of the pattern has.</summary>
    public List<string> Unusual { get; init; } = [];

    /// <summary>Gets the instance's companion in each other pattern that has one, by that pattern's identifier.</summary>
    public SortedDictionary<string, string> Companions { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Gets the instance's nearest and typical examples.</summary>
    public ExampleChoice Examples { get; init; } = new(null, null);

    /// <summary>Gets the rows that align the instance with each other type whose source the pattern carries, by that type's name.</summary>
    public SortedDictionary<string, List<CompareRow>> Compare { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>An instance of a pattern at head that the pattern does not report as added or changed, with the leaf it binds to at head.</summary>
public sealed record PatternOther(string Type, string Leaf);

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
/// only its leaves at head. Test files count apart: the tests count is the number of changed test files whose subjects
/// bind to a leaf under the node, and at the top level the number of every changed test file.
/// </summary>
public sealed record NodeContent(int Files, int Added, int Removed, int Modified, string Status, int Tests = 0);

/// <summary>
/// A file's change status and the leaves its types bound to at base and at head. The status is <c>added</c>,
/// <c>removed</c>, <c>modified</c>, <c>renamed</c> or <c>unchanged</c>. The path is the file's path at head, or at base
/// for a removed file, and a renamed file also has its path at base. A changed file that no type binds has no leaves at
/// either end. The blobs are git's object identifiers of the file at each end, when git reports the file as changed.
/// A test file, which declares test types and no type of the system, has no leaves either, and is marked as one with
/// its test types' subject leaf, as <see cref="FileFact"/> marks it.
/// </summary>
public sealed record FileChange(string Path, string Status, IReadOnlyList<string> LeavesBase, IReadOnlyList<string> LeavesHead)
{
    /// <summary>Gets a value indicating whether the file is a test file at either end.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Test { get; init; }

    /// <summary>Gets the leaf of a test file's subject at head, or at base when the file was removed, or <see langword="null"/> for test infrastructure and any file that is not a test file.</summary>
    public string? Subject { get; init; }

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

    /// <summary>Returns the lines of the file at head that the hunks hold, context and added lines alike, keyed by one-based line number.</summary>
    public Dictionary<int, string> LinesAtHead()
    {
        var result = new Dictionary<int, string>();
        foreach (var hunk in Hunks)
        {
            if (!hunk.TryRanges(out _, out var now)) continue;
            var line = now.Start + 1;
            foreach (var l in hunk.Lines)
                if (l[0] != "-") result[line++] = l[1];
        }
        return result;
    }
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

    /// <summary>Gets the test signals the hunk shows, as <see cref="TestSignals"/> finds them, or <see langword="null"/> when it shows none.</summary>
    public List<TestSignal>? Signals { get; init; }

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

    /// <summary>
    /// Gets the test methods that reference the decision's declarations, with their statuses and ways, the removed
    /// first, then the added, the changed and the unchanged, as <see cref="TestChanges"/> finds them.
    /// </summary>
    public List<DecisionTest> Tests { get; init; } = [];
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
