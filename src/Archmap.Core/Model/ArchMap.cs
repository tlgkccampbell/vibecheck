using System.Text.Json.Serialization;

namespace Archmap.Model;

/// <summary>
/// The architecture map: the checked-in specification of a system's nodes, the code
/// each leaf binds, the edges the code is permitted to realize, and the policies that
/// constrain both. See <c>docs/archmap-spec.md</c>.
/// </summary>
public sealed class ArchMap
{
    /// <summary>The edge policy under which every observed edge among a level's children must be declared and every declared edge realized.</summary>
    public const string Declared = "declared";

    /// <summary>The edge policy under which edges among a level's children are drawn from the code and not checked.</summary>
    public const string Observed = "observed";

    public int Version { get; init; }

    /// <summary>The solution file, relative to the repository root.</summary>
    public required string Solution { get; init; }

    public SortedDictionary<string, NodeSpec> Nodes { get; init; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, ExternalSpec> Externals { get; init; } = new(StringComparer.Ordinal);

    public List<EdgeSpec> Edges { get; init; } = [];

    public PolicySpec Policies { get; init; } = new();

    /// <summary>Reports whether an identifier names a node or an external.</summary>
    public bool Exists(string id) => id == Identifiers.Root || Nodes.ContainsKey(id) || Externals.ContainsKey(id);

    /// <summary>Reports whether an identifier names a container, which is the root or a node without bindings.</summary>
    public bool IsContainer(string id) =>
        id == Identifiers.Root || (Nodes.TryGetValue(id, out var n) && !n.IsLeaf);

    /// <summary>Returns the edge policy that governs edges among a level's children.</summary>
    public string PolicyOf(string level) =>
        level == Identifiers.Root ? Policies.Root.Edges : Nodes[level].Edges ?? Observed;

    /// <summary>Returns the nodes whose parent is the given level, and at the root level the externals as well.</summary>
    public IEnumerable<string> ChildrenOf(string level)
    {
        foreach (var id in Nodes.Keys)
        {
            if (Identifiers.ParentOrRoot(id) == level) yield return id;
        }
        if (level == Identifiers.Root)
        {
            foreach (var id in Externals.Keys) yield return id;
        }
    }

    /// <summary>Returns the leaves beneath a level, or the level itself when it is a leaf.</summary>
    public IEnumerable<string> LeavesUnder(string level) =>
        Nodes.Where(kv => kv.Value.IsLeaf && Identifiers.IsUnder(kv.Key, level)).Select(kv => kv.Key);

    /// <summary>Finds the declared edge between two identifiers, if any.</summary>
    public EdgeSpec? FindEdge(string from, string to) =>
        Edges.FirstOrDefault(e => e.From == from && e.To == to);

    /// <summary>Returns a deep copy of the map.</summary>
    public ArchMap Clone()
    {
        var copy = new ArchMap
        {
            Version = Version,
            Solution = Solution,
            Policies = new PolicySpec
            {
                Root = Policies.Root,
                Forbid = [.. Policies.Forbid],
                Acyclic = [.. Policies.Acyclic],
            },
        };
        foreach (var (k, v) in Nodes) copy.Nodes[k] = v;
        foreach (var (k, v) in Externals) copy.Externals[k] = v;
        copy.Edges.AddRange(Edges);
        return copy;
    }
}

/// <summary>A node of the map. A node with bindings is a leaf; a node without is a container.</summary>
public sealed record NodeSpec
{
    public required string Name { get; init; }
    public string? Purpose { get; init; }
    public string? Note { get; init; }

    /// <summary>The edge policy among this node's children, when the node is a container. Absent means observed.</summary>
    public string? Edges { get; init; }

    /// <summary>Path globs relative to the repository root. Present on leaves only.</summary>
    public IReadOnlyList<string>? Bind { get; init; }

    [JsonIgnore]
    public bool IsLeaf => Bind is not null;
}

/// <summary>An external assembly or family of assemblies, bound by assembly name patterns.</summary>
public sealed record ExternalSpec
{
    public required string Name { get; init; }
    public string? Purpose { get; init; }
    public required IReadOnlyList<string> Bind { get; init; }
}

/// <summary>A declared dependency between two siblings.</summary>
public sealed record EdgeSpec
{
    public required string From { get; init; }
    public required string To { get; init; }
    public string? Label { get; init; }

    /// <summary>The simple name of a type that the edge carries, which rule 5 requires to be referenced across it.</summary>
    public string? Payload { get; init; }

    /// <summary>Marks an edge the checker cannot see, which is exempt from realization.</summary>
    public bool Asserted { get; init; }
}

public sealed record PolicySpec
{
    public RootPolicy Root { get; init; } = new();
    public List<ForbidSpec> Forbid { get; init; } = [];
    public List<string> Acyclic { get; init; } = [];
}

public sealed record RootPolicy
{
    public string Edges { get; init; } = ArchMap.Observed;
    public string Externals { get; init; } = ArchMap.Observed;
}

public sealed record ForbidSpec
{
    public required string From { get; init; }
    public required string To { get; init; }
}
