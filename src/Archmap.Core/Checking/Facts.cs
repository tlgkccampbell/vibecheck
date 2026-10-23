using System.Text.Json;
using System.Text.Json.Serialization;
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
    public required List<FileFact> Files { get; init; }
    public required List<DependencyFact> Dependencies { get; init; }
    public required List<ExternalRefFact> ExternalRefs { get; init; }
    public required List<UnresolvableFact> Unresolvable { get; init; }
    public required SortedDictionary<string, NodeMetrics> Metrics { get; init; }
    public required List<ViolationFact> Violations { get; init; }
    public List<string> Warnings { get; init; } = [];

    [JsonIgnore]
    public bool Passed => Violations.Count == 0;
}

/// <summary>A compiled file and the leaf it binds to, which is absent when the partition rule failed for it.</summary>
public sealed record FileFact(string Path, string? Leaf, string Hash);

/// <summary>
/// An observed dependency lifted to the level where it is checked and drawn. For a
/// dependency between files of the same leaf, the level is the leaf and the edge is
/// absent.
/// </summary>
public sealed record DependencyFact(string From, string To, string Kind, string Symbol, string Type, int Line, string Level, string[]? Edge);

/// <summary>A reference to an external assembly, resolved to the external it binds to, if any, and lifted to the top-level node of the referencing file.</summary>
public sealed record ExternalRefFact(string From, string Assembly, string Symbol, int Line, string? External, string TopLevel);

public sealed record UnresolvableFact(string From, string Symbol, string Generator, int Line);

/// <summary>Counts for curation: dependencies that stay inside a node, dependencies that cross its boundary, and cycles among its children.</summary>
public sealed record NodeMetrics(int Internal, int Crossing, List<List<string>> Cycles);

/// <summary>A failed rule, with the evidence that failed it and the map edit that would resolve it when one exists.</summary>
public sealed record ViolationFact(string Rule, string Message, object? Evidence, ProposalFact? Proposal);

/// <summary>A map edit the checker proposes. The operation is <c>add-edge</c>, <c>remove-edge</c> or <c>add-leaf</c>.</summary>
public sealed record ProposalFact(string Op, string? From = null, string? To = null, string? Node = null, string? Name = null, IReadOnlyList<string>? Bind = null);

/// <summary>Reads and writes facts and changeset documents as JSON.</summary>
public static class FactsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    public static T Deserialize<T>(string text) =>
        JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("empty document");
}
