using System.Text.Json.Serialization;

namespace Vibecheck.Model;

/// <summary>
/// The architecture map: the checked-in description of a system's nodes, the code
/// each leaf binds, the views that regroup leaves, the types the map excludes, the
/// types it reads as tests, and the patterns it names, declares or ignores.
/// Edges are never part of the map; they are read from the code. See
/// <c>docs/vibecheck-spec.md</c>.
/// </summary>
public sealed class ArchitectureMap
{
    /// <summary>The identifier of the automatic leaf that holds every type no declared leaf binds.</summary>
    public const string Unsorted = "unsorted";

    public int Version { get; init; }

    /// <summary>The solution file, relative to the repository root.</summary>
    public required string Solution { get; init; }

    /// <summary>Type patterns naming the types that are not part of the system: not partitioned, not drawn, and creating no edges.</summary>
    public List<string> Exclude { get; init; } = [];

    /// <summary>
    /// Gets the type patterns naming the test types: types that bind to nothing and create no edges, as excluded types do,
    /// but that the analysis keeps as evidence about the system. See <c>docs/tests.md</c> section 3.1.
    /// </summary>
    public List<string> Tests { get; init; } = [];

    /// <summary>Gets the entries that name, declare or ignore patterns, in the order written. See <c>docs/patterns.md</c> section 4.4.</summary>
    public List<PatternSpec> Patterns { get; init; } = [];

    public SortedDictionary<string, NodeSpec> Nodes { get; init; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, ViewSpec> Views { get; init; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, ExternalSpec> Externals { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Reports whether an identifier names a node or an external.</summary>
    public bool Exists(string id) => id == Identifiers.Root || Nodes.ContainsKey(id) || Externals.ContainsKey(id);

    /// <summary>Reports whether an identifier names a container, which is the root or a node without bindings.</summary>
    public bool IsContainer(string id) =>
        id == Identifiers.Root || (Nodes.TryGetValue(id, out var n) && !n.IsLeaf);

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

    /// <summary>Returns the declared leaves beneath a level, or the level itself when it is a leaf.</summary>
    public IEnumerable<string> LeavesUnder(string level) =>
        Nodes.Where(kv => kv.Value.IsLeaf && Identifiers.IsUnder(kv.Key, level)).Select(kv => kv.Key);

    /// <summary>
    /// Returns the category of a node: its own, or else that of its nearest ancestor that has one, or
    /// <see langword="null"/> when no node on its path has a category.
    /// </summary>
    public string? CategoryOf(string id)
    {
        for (var x = id; x != Identifiers.Root; x = Identifiers.ParentOrRoot(x))
        {
            if (Nodes.TryGetValue(x, out var n) && n.Category is not null) return n.Category;
        }
        return null;
    }

    /// <summary>Returns a deep copy of the map.</summary>
    public ArchitectureMap Clone()
    {
        var copy = new ArchitectureMap { Version = Version, Solution = Solution, Exclude = [.. Exclude], Tests = [.. Tests], Patterns = [.. Patterns] };
        foreach (var (k, v) in Nodes) copy.Nodes[k] = v;
        foreach (var (k, v) in Views) copy.Views[k] = v;
        foreach (var (k, v) in Externals) copy.Externals[k] = v;
        return copy;
    }
}

/// <summary>A node of the map. A node with bindings is a leaf; a node without is a container.</summary>
public sealed record NodeSpec
{
    public required string Name { get; init; }
    public string? Purpose { get; init; }

    /// <summary>
    /// Gets the broad category of the node's role, one of <see cref="Categories.Names"/>, or <see langword="null"/>
    /// when the node takes the category of its nearest ancestor that has one.
    /// </summary>
    public string? Category { get; init; }

    public string? Note { get; init; }

    /// <summary>Type patterns, as section 2.2 of the specification defines them. Present on leaves only.</summary>
    public IReadOnlyList<string>? Bind { get; init; }

    [JsonIgnore]
    public bool IsLeaf => Bind is not null;
}

/// <summary>An alternative grouping of the leaves under one level, as section 2.8 of the specification defines it.</summary>
public sealed record ViewSpec
{
    public required string Name { get; init; }
    public string? Purpose { get; init; }

    /// <summary>The level the view regroups: <see cref="Identifiers.Root"/> or a container identifier.</summary>
    public required string Level { get; init; }

    public SortedDictionary<string, GroupSpec> Groups { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>One group of a view: a name, an optional purpose, and the leaves it holds.</summary>
public sealed record GroupSpec
{
    public required string Name { get; init; }
    public string? Purpose { get; init; }
    public required IReadOnlyList<string> Leaves { get; init; }
}

/// <summary>
/// An entry of the map's patterns list. An entry with <see cref="Keys"/> names the pattern that discovery finds with those
/// keys, or declares the pattern when discovery does not find it. An entry with <see cref="Ignore"/> drops the group of
/// that key from discovery. See <c>docs/patterns.md</c> section 4.4.
/// </summary>
public sealed record PatternSpec
{
    /// <summary>Gets the pattern's identifier, a single segment, which an entry with keys requires.</summary>
    public string? Id { get; init; }

    public string? Name { get; init; }
    public string? Purpose { get; init; }

    /// <summary>Gets the keys of the pattern the entry names or declares, as <see cref="PatternKeys"/> writes them, or <see langword="null"/> for an entry that ignores a key.</summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary>Gets the key whose group discovery drops, or <see langword="null"/> for an entry with keys.</summary>
    public string? Ignore { get; init; }
}

/// <summary>An external assembly or family of assemblies, bound by assembly name patterns.</summary>
public sealed record ExternalSpec
{
    public required string Name { get; init; }
    public string? Purpose { get; init; }
    public required IReadOnlyList<string> Bind { get; init; }
}
