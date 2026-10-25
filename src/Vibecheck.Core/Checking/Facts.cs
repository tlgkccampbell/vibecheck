using System.Text.Json;
using System.Text.Json.Serialization;
using Vibecheck.Analysis;
using Vibecheck.Model;

namespace Vibecheck.Checking;

/// <summary>
/// The facts document: everything the checker established about one commit. The
/// viewer consumes two of these and never reads the code or the map directly. See
/// <c>docs/vibecheck-spec.md</c> section 6.
/// </summary>
public sealed record Facts
{
    public string? Commit { get; init; }
    public required ArchitectureMap Map { get; init; }

    /// <summary>How many types and files the map's exclude list removed before anything else was considered.</summary>
    public required ExcludedCounts Excluded { get; init; }

    public required List<TypeFact> Types { get; init; }
    public required List<FileFact> Files { get; init; }
    public required List<DependencyFact> Dependencies { get; init; }

    /// <summary>The dependencies aggregated per pair of leaves, and per leaf and external. Every drawing of an edge starts here.</summary>
    public required List<EdgeCount> LeafEdges { get; init; }

    public required List<ExternalRefFact> ExternalRefs { get; init; }
    public required List<UnresolvableFact> Unresolvable { get; init; }

    /// <summary>Metrics per node, including <see cref="ArchitectureMap.Unsorted"/> when it holds anything.</summary>
    public required SortedDictionary<string, NodeMetrics> Nodes { get; init; }

    /// <summary>The edges among each level's children and the cycles among them, keyed by the level.</summary>
    public required SortedDictionary<string, LevelMetrics> Levels { get; init; }

    /// <summary>The same metrics for each view's groups, keyed by the view.</summary>
    public required SortedDictionary<string, ViewMetrics> Views { get; init; }

    public required List<ViolationFact> Violations { get; init; }
    public List<string> Warnings { get; init; } = [];

    /// <summary>Gets the declarations of the types the map holds, as <see cref="AnalysisResult.Declarations"/> lists them.</summary>
    public List<Declaration> Declarations { get; init; } = [];

    /// <summary>Gets the sites among the types the map holds, as <see cref="AnalysisResult.Sites"/> lists them. Every site of a declaration is here or in <see cref="Dependencies"/>.</summary>
    public List<Site> Sites { get; init; } = [];

    /// <summary>Gets the test types, with their subjects and test methods, and the references they make into the system, as <see cref="TestTypes"/> builds them.</summary>
    public TestFacts Tests { get; init; } = TestFacts.Empty;

    /// <summary>Gets the patterns among the types, most instances first, as <see cref="PatternDiscovery"/> finds them.</summary>
    public List<PatternFact> Patterns { get; init; } = [];

    [JsonIgnore]
    public bool Passed => Violations.Count == 0;
}

/// <summary>
/// A top-level type, the leaf it binds to, how it was bound (<c>attribute</c>, <c>pattern</c> or <c>fallback</c>), the
/// files that declare it, the generator that produced a part of it, and its traits, as <see cref="TypeInfo.Traits"/> gives them.
/// </summary>
public sealed record TypeFact(string Name, string Assembly, string Leaf, string By, IReadOnlyList<string> Files, string? Generator = null, TypeTraits? Traits = null);

/// <summary>A pattern among the types of a commit, as <see cref="PatternDiscovery"/> finds it. See <c>docs/patterns.md</c> section 4.</summary>
/// <param name="Id">The identifier of the map's entry for the pattern, or else its keys joined by <c>" + "</c>.</param>
/// <param name="Name">The name the map's entry gives the pattern, or else the name its first key gives it, as <see cref="PatternKeys.NameOf"/> forms it.</param>
/// <param name="Purpose">The purpose the map's entry gives the pattern, or <see langword="null"/>.</param>
/// <param name="By">What named the pattern: <see cref="PatternDiscovery.ByMap"/> or <see cref="PatternDiscovery.ByKey"/>.</param>
/// <param name="Keys">The keys every instance carries, ordered by kind as <see cref="PatternKeys.Kinds"/> lists them and then by value.</param>
/// <param name="Instances">The names of the types that carry every key, ordered by name.</param>
public sealed record PatternFact(string Id, string Name, string? Purpose, string By, IReadOnlyList<string> Keys, IReadOnlyList<string> Instances)
{
    /// <summary>Gets the pattern's norms, most shared first, as <see cref="PatternFeatures.Norms"/> finds them.</summary>
    public List<NormFact> Norms { get; init; } = [];

    /// <summary>Gets the mean number of norms the pattern's own instances lack, which says whether the norm thresholds suit it.</summary>
    public double MeanDepartures { get; init; }
}

/// <summary>A norm of a pattern: a feature, as <see cref="PatternFeatures"/> names features, and how many of the pattern's instances have it.</summary>
public sealed record NormFact(string Feature, int Share);

/// <summary>
/// A source document, its content hash, and the leaves its types bind to. A generated document names its generator. A
/// file that declares test types and no system type has no leaves, and is marked as a test file with the leaf of its
/// test types' subject.
/// </summary>
public sealed record FileFact(string Path, string Hash, IReadOnlyList<string> Leaves, string? Generator = null)
{
    /// <summary>Gets a value indicating whether the file declares test types and no type of the system.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Test { get; init; }

    /// <summary>Gets the leaf of the subject of the file's test types, or <see langword="null"/> for a file of test infrastructure or any file that is not a test file.</summary>
    public string? Subject { get; init; }
}

/// <summary>The test types of a commit and the references they make into the system. See <c>docs/tests.md</c> section 3.2.</summary>
/// <param name="Types">The test types, ordered by name.</param>
/// <param name="Exercises">The references from test types to declarations of the system, ordered by file and line.</param>
/// <param name="Calls">The references from members of test types to members of test types, ordered by file and line.</param>
/// <param name="Declarations">
/// The declarations of the test types, as <see cref="AnalysisResult.Declarations"/> lists them, which tell a renamed or
/// moved test method from a removed one.
/// </param>
public sealed record TestFacts(List<TestTypeFact> Types, List<ExerciseFact> Exercises, List<CallFact> Calls, List<Declaration> Declarations)
{
    /// <summary>The test facts of a commit that has no test types.</summary>
    public static TestFacts Empty => new([], [], [], []);
}

/// <summary>
/// A test type: a type that the map's <c>tests</c> list matches and its <c>exclude</c> list does not. It binds to no leaf.
/// Its subject is the system type it tests, which places it in the view, and a test type without one is test
/// infrastructure, such as a fixture, a builder or a helper. See <c>docs/tests.md</c> section 4.
/// </summary>
/// <param name="Name">The type's name, as <see cref="TypeFact.Name"/> gives it.</param>
/// <param name="Assembly">The assembly that declares the type.</param>
/// <param name="Files">The files that declare the type.</param>
/// <param name="Subject">The system type the test type tests, or <see langword="null"/> for test infrastructure.</param>
/// <param name="Methods">The test type's test methods, ordered by file and line.</param>
public sealed record TestTypeFact(string Name, string Assembly, IReadOnlyList<string> Files, SubjectFact? Subject, List<TestMethodFact> Methods);

/// <summary>The system type a test type tests, the leaf that type binds to, and the rule that decided it: <c>name</c> or <c>references</c>.</summary>
public sealed record SubjectFact(string Type, string Leaf, string By);

/// <summary>
/// A test method: a method of a test type that carries a test framework attribute, as <see cref="Declaration.Test"/>
/// names it. The symbol is the method's, the lines are those its declaration covers in its file, one-based, and the
/// assertions are the lines of its assertion calls, as <see cref="Declaration.Assertions"/> gives them.
/// </summary>
public sealed record TestMethodFact(string Symbol, string File, int First, int Last, bool Skipped, IReadOnlyList<LineRange> Assertions);

/// <summary>
/// An exercise: a reference from a test type to a declaration of the system, kept apart from the dependencies so that
/// it never becomes an edge. The method is the member of the test type whose lines hold the reference, when one does.
/// </summary>
public sealed record ExerciseFact(string From, string? Method, string Symbol, string File, int Line);

/// <summary>
/// A call: a reference from a member of a test type to a declaration of a test type, the same one or another, which is
/// what <see cref="TestReferences"/> follows through helpers. The member is the innermost one whose lines hold the
/// reference. See <c>docs/tests.md</c> section 5.1.
/// </summary>
public sealed record CallFact(string From, string To, string File, int Line);

/// <summary>An observed dependency between two types, kept as evidence. The leaves of its ends are found through the types list.</summary>
public sealed record DependencyFact(string From, string To, string Kind, string Symbol, string File, int Line);

/// <summary>A count of references from one node or group to another.</summary>
public sealed record EdgeCount(string From, string To, int Count);

/// <summary>
/// The references one type makes to one type of an external assembly, resolved to the external the assembly binds to,
/// if any. The referenced type is in the open form that <see cref="TraitType"/> describes. The kind, symbol, file and
/// line are those of the first reference, and the count covers all of them. A reader that counts references per
/// assembly sums the counts.
/// </summary>
public sealed record ExternalRefFact(string From, string Assembly, string Type, string Kind, string Symbol, string File, int Line, int Count, string? External);

public sealed record UnresolvableFact(string From, string Symbol, string Reason, string File, int Line);

/// <summary>
/// Counts for a node or a group: the types it holds, the references that stay inside
/// it, the references that cross its boundary, and the types bound by attribute.
/// </summary>
public sealed record NodeMetrics(int Types, int Internal, int Crossing, int Attributed);

/// <summary>The edges among a level's children with their counts, and the cycles among them.</summary>
public sealed record LevelMetrics(List<EdgeCount> Edges, List<List<string>> Cycles);

/// <summary>The metrics of a view: per group, among groups, and the cycles among groups.</summary>
public sealed record ViewMetrics(SortedDictionary<string, NodeMetrics> Groups, List<EdgeCount> Edges, List<List<string>> Cycles);

/// <summary>A failure that makes the map unusable, with the evidence behind it.</summary>
public sealed record ViolationFact(string Rule, string Message, object? Evidence = null);

/// <summary>Reads and writes facts and changeset documents as JSON.</summary>
public static class FactsJson
{
    /// <summary>Compact output. A facts document for a large solution carries tens of thousands of dependencies, and indentation would double its size.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    public static T Deserialize<T>(string text) =>
        JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("empty document");
}
