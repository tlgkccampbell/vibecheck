using System.Text.Json;
using System.Text.Json.Serialization;
using Archmap.Analysis;
using Archmap.Model;

namespace Archmap.Checking;

/// <summary>
/// The facts document: everything the checker established about one commit. The
/// viewer consumes two of these and never reads the code or the map directly. See
/// <c>docs/archmap-spec.md</c> section 6.
/// </summary>
public sealed record Facts
{
    public string? Commit { get; init; }
    public required ArchMap Map { get; init; }

    /// <summary>How many types and files the map's exclude list removed before anything else was considered.</summary>
    public required ExcludedCounts Excluded { get; init; }

    public required List<TypeFact> Types { get; init; }
    public required List<FileFact> Files { get; init; }
    public required List<DependencyFact> Dependencies { get; init; }

    /// <summary>The dependencies aggregated per pair of leaves, and per leaf and external. Every drawing of an edge starts here.</summary>
    public required List<EdgeCount> LeafEdges { get; init; }

    public required List<ExternalRefFact> ExternalRefs { get; init; }
    public required List<UnresolvableFact> Unresolvable { get; init; }

    /// <summary>Metrics per node, including <see cref="ArchMap.Unsorted"/> when it holds anything.</summary>
    public required SortedDictionary<string, NodeMetrics> Nodes { get; init; }

    /// <summary>The edges among each level's children and the cycles among them, keyed by the level.</summary>
    public required SortedDictionary<string, LevelMetrics> Levels { get; init; }

    /// <summary>The same metrics for each view's groups, keyed by the view.</summary>
    public required SortedDictionary<string, ViewMetrics> Views { get; init; }

    public required List<ViolationFact> Violations { get; init; }
    public List<string> Warnings { get; init; } = [];

    [JsonIgnore]
    public bool Passed => Violations.Count == 0;
}

/// <summary>A top-level type, the leaf it binds to, how it was bound (<c>attribute</c>, <c>pattern</c> or <c>fallback</c>), the files that declare it, and the generator that produced a part of it.</summary>
public sealed record TypeFact(string Name, string Assembly, string Leaf, string By, IReadOnlyList<string> Files, string? Generator = null);

/// <summary>A source document, its content hash, and the leaves its types bind to. A generated document names its generator.</summary>
public sealed record FileFact(string Path, string Hash, IReadOnlyList<string> Leaves, string? Generator = null);

/// <summary>An observed dependency between two types, kept as evidence. The leaves of its ends are found through the types list.</summary>
public sealed record DependencyFact(string From, string To, string Kind, string Symbol, string File, int Line);

/// <summary>A count of references from one node or group to another.</summary>
public sealed record EdgeCount(string From, string To, int Count);

/// <summary>
/// The references one type makes to one external assembly, resolved to the external
/// the assembly binds to, if any. The symbol, file and line are the first reference;
/// the count covers all of them.
/// </summary>
public sealed record ExternalRefFact(string From, string Assembly, string Symbol, string File, int Line, int Count, string? External);

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
