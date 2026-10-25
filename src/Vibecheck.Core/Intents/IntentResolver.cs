using Vibecheck.Analysis;
using Vibecheck.Diffing;
using Vibecheck.Model;
using Vibecheck.Reviewing;

namespace Vibecheck.Intents;

/// <summary>
/// Represents an anchor of a <see cref="ResolvedIntent"/>, with what it resolved to and whether the change touches it.
/// See <c>docs/intent.md</c> section 4.1.
/// </summary>
/// <param name="Text">The anchor's text as written.</param>
/// <param name="Kind">The kind of thing the anchor names: <c>node</c>, <c>edge</c>, <c>file</c> or <c>type</c>.</param>
/// <param name="State">One of <see cref="IntentResolver.Touched"/>, <see cref="IntentResolver.Untouched"/> and <see cref="IntentResolver.Unknown"/>.</param>
public sealed record ResolvedAnchor(string Text, string Kind, string State)
{
    /// <summary>
    /// Gets what the anchor resolved to: a node identifier, a file's path relative to the changeset's root, a type's
    /// name as the changeset gives it, or an edge's two ends so resolved and joined by <c>→</c>. It is
    /// <see langword="null"/> for an unknown anchor.
    /// </summary>
    public string? Target { get; init; }
}

/// <summary>
/// Represents an <see cref="Intent"/> resolved against a changeset: its parts, and each anchor with what it names and
/// whether the change touches it. See <c>docs/intent.md</c> section 4.3.
/// </summary>
/// <param name="Line">The 1-based line of the description on which the intent starts.</param>
/// <param name="Claim">What was chosen, as Markdown whose only markup is inline code.</param>
/// <param name="Why">The reason given, or <see langword="null"/>.</param>
/// <param name="Alternative">What it was chosen instead of, or <see langword="null"/>.</param>
/// <param name="Note">The item's other text, or <see langword="null"/>.</param>
/// <param name="Anchors">The anchors, in the order written.</param>
public sealed record ResolvedIntent(int Line, string Claim, string? Why, string? Alternative, string? Note, List<ResolvedAnchor> Anchors)
{
    /// <summary>
    /// Gets a value indicating whether the intent is unbacked: it has anchors, and the change touches none of them. An
    /// unbacked intent is a fact to show the reviewer, not an error.
    /// </summary>
    public bool Unbacked => Anchors.Count > 0 && Anchors.All(a => a.State != IntentResolver.Touched);
}

/// <summary>
/// Resolves the anchors of intents against a changeset. It is a pure function of the intents and the changeset, as
/// <see cref="ReadingPath.Plan"/> is, so resolving again when the description changes costs nothing worth caching.
/// See <c>docs/intent.md</c> section 4.1.
/// </summary>
public static class IntentResolver
{
    /// <summary>The state of an anchor that names something the change touches.</summary>
    public const string Touched = "touched";

    /// <summary>The state of an anchor that names something the change leaves alone.</summary>
    public const string Untouched = "untouched";

    /// <summary>The state of an anchor that names nothing in the map or the code.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Returns the intents with each anchor resolved against a changeset. Node identifiers resolve only against the
    /// repository's own map, since a generated map's identifiers are not the ones its author could name.
    /// </summary>
    /// <param name="intents">The intents, as <see cref="IntentReader.Parse"/> reads them.</param>
    /// <param name="changeset">The changeset to resolve against.</param>
    /// <param name="root">The directory the changeset's paths are relative to, relative to the repository's top.</param>
    /// <param name="ownMap">A value indicating whether the changeset is drawn with the repository's own map.</param>
    public static List<ResolvedIntent> Resolve(IReadOnlyList<Intent> intents, Changeset changeset, string root, bool ownMap) =>
        Resolve(new Scope(changeset, root, ownMap), intents);

    private static List<ResolvedIntent> Resolve(Scope scope, IReadOnlyList<Intent> intents) =>
        intents.Select(i => new ResolvedIntent(i.Line, i.Claim, i.Why, i.Alternative, i.Note, i.Anchors.Select(scope.Resolve).ToList())).ToList();

    /// <summary>
    /// Returns a changeset with the intents resolved against it, the intents that cover each decision, and the coverage
    /// of its reading path's surprises. Each surprise carries the indices of the intents that cover it, or is marked
    /// uncovered when none does. A changeset given no intents keeps its decisions and path as they are, since a change
    /// with no intent section is not missing one. The path's order never changes. See <c>docs/intent.md</c> section 4.2.
    /// </summary>
    /// <param name="changeset">The changeset to resolve against, which is left as it is.</param>
    /// <param name="intents">The intents, as <see cref="IntentReader.Parse"/> reads them.</param>
    /// <param name="root">The directory the changeset's paths are relative to, relative to the repository's top.</param>
    /// <param name="ownMap">A value indicating whether the changeset is drawn with the repository's own map.</param>
    public static Changeset Apply(Changeset changeset, IReadOnlyList<Intent> intents, string root, bool ownMap)
    {
        var scope = new Scope(changeset, root, ownMap);
        var resolved = Resolve(scope, intents);
        if (resolved.Count == 0) return changeset with { Intents = resolved };
        List<int>? Covering(Func<ResolvedAnchor, bool> covers) =>
            Enumerable.Range(0, resolved.Count).Where(i => resolved[i].Anchors.Any(covers)).ToList() is { Count: > 0 } found ? found : null;
        var decisions = changeset.Decisions.Select(d => d with { Intents = Covering(a => scope.Covers(a, d)) }).ToList();
        var path = changeset.Path.Select(step =>
        {
            if (!IsSurprise(step)) return step;
            var covering = Covering(a => scope.Covers(a, step));
            return step with { Intents = covering, Uncovered = covering is null };
        }).ToList();
        return changeset with { Intents = resolved, Decisions = decisions, Path = path };
    }

    /// <summary>
    /// Gets a value indicating whether a step of the reading path is a surprise: a first-tier new edge, which includes
    /// an edge out of a foundation or domain block, a new cycle, or a decision. Steps for tests and patterns are not,
    /// since their signal is a departure from a norm rather than a choice.
    /// </summary>
    public static bool IsSurprise(PathStep step) => step.Tier == "first" && step.Kind is "edge" or "cycle" or "decision";

    /// <summary>Returns a type's name without its assembly prefix or its type parameters, which is how an anchor names it.</summary>
    private static string Bare(string name)
    {
        var plain = TypeNames.Plain(name);
        var cut = plain.IndexOfAny(['<', '`']);
        return cut < 0 ? plain : plain[..cut];
    }

    private static string SimpleOf(string bare) => bare[(bare.LastIndexOf('.') + 1)..];

    /// <summary>Represents what a changeset says about the things anchors can name, indexed once for every anchor.</summary>
    private sealed class Scope
    {
        private readonly Changeset changeset;
        private readonly string root;
        private readonly bool ownMap;
        private readonly HashSet<string> changedLeaves = new(StringComparer.Ordinal);
        private readonly HashSet<string> nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FileChange> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> declared = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TypeChange>> byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TypeChange>> bySimpleName = new(StringComparer.Ordinal);
        private readonly List<EdgeChange> changedEdges;

        // What each resolved anchor covers, by the anchor itself: the edge and cycle steps of the path for an edge anchor,
        // and the decisions for a file or type anchor. A node anchor covers nothing.
        private readonly Dictionary<ResolvedAnchor, Func<PathStep, bool>> covers = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ResolvedAnchor, Func<Decision, bool>> decides = new(ReferenceEqualityComparer.Instance);

        public Scope(Changeset changeset, string root, bool ownMap)
        {
            (this.changeset, this.root, this.ownMap) = (changeset, root, ownMap);
            foreach (var f in changeset.Files)
            {
                files[f.Path] = f;
                if (f.PreviousPath is not null) files.TryAdd(f.PreviousPath, f);
                if (f.Status != "unchanged") changedLeaves.UnionWith(f.LeavesBase.Concat(f.LeavesHead));
            }
            nodes.UnionWith(changeset.Map.Nodes.Keys);
            nodes.UnionWith(changeset.Map.Externals.Keys);
            nodes.UnionWith(changeset.Structural.Nodes.Select(n => n.Id));
            foreach (var t in changeset.Types)
            {
                declared.UnionWith(t.Files);
                var bare = Bare(t.Name);
                Add(byName, bare, t);
                Add(bySimpleName, SimpleOf(bare), t);
            }
            changedEdges = changeset.Edges.Where(e => e.Status != "unchanged").ToList();
        }

        private static void Add(Dictionary<string, List<TypeChange>> index, string key, TypeChange type)
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = [];
            list.Add(type);
        }

        public ResolvedAnchor Resolve(IntentAnchor anchor) => anchor.Kind switch
        {
            IntentAnchorKind.Node => Node(anchor),
            IntentAnchorKind.Edge => Edge(anchor),
            IntentAnchorKind.File => File(anchor),
            _ => Type(anchor),
        };

        /// <summary>Gets a value indicating whether a resolved anchor covers a surprise: it is touched, and names the surprise as <c>docs/intent.md</c> section 4.2 lists.</summary>
        public bool Covers(ResolvedAnchor anchor, PathStep step) => step.Kind == "decision"
            ? DecisionOf(step) is { } d && Covers(anchor, d)
            : anchor.State == Touched && covers.TryGetValue(anchor, out var f) && f(step);

        /// <summary>Gets a value indicating whether a resolved anchor covers a decision: it is touched, and is the file that holds the decision's hunk or a type the decision declares.</summary>
        public bool Covers(ResolvedAnchor anchor, Decision decision) => anchor.State == Touched && decides.TryGetValue(anchor, out var f) && f(decision);

        private ResolvedAnchor Make(IntentAnchor anchor, string? target, bool touched, Func<PathStep, bool>? covering = null, Func<Decision, bool>? deciding = null)
        {
            ResolvedAnchor resolved = target is null ? new(anchor.Text, KindOf(anchor), Unknown) : new(anchor.Text, KindOf(anchor), touched ? Touched : Untouched) { Target = target };
            if (target is null) return resolved;
            if (covering is not null) covers[resolved] = covering;
            if (deciding is not null) decides[resolved] = deciding;
            return resolved;
        }

        private static string KindOf(IntentAnchor anchor) => anchor.Kind.ToString().ToLowerInvariant();

        /// <summary>
        /// Resolves a node anchor. A node is touched when it or a leaf under it has a changed file, when the change edits
        /// it or a node under it in the map, or, for an external, when an edge to it changed.
        /// </summary>
        private ResolvedAnchor Node(IntentAnchor anchor)
        {
            var id = anchor.Name;
            if (!IsNode(id)) return Make(anchor, null, false);
            var touched = changedLeaves.Any(l => Identifiers.IsUnder(l, id))
                || changeset.Structural.Nodes.Any(n => Identifiers.IsUnder(n.Id, id))
                || (changeset.Map.Externals.ContainsKey(id) && changedEdges.Any(e => e.To == id));
            return Make(anchor, id, touched);
        }

        private bool IsNode(string id) => ownMap && nodes.Contains(id);

        /// <summary>
        /// Resolves an edge anchor. An edge between blocks is touched when a changed leaf edge runs from under one to
        /// under the other, which every level that draws both blocks shows as a changed edge. An edge with a type end is
        /// touched when the evidence of a changed edge holds a type pair that matches both ends.
        /// </summary>
        private ResolvedAnchor Edge(IntentAnchor anchor)
        {
            var from = End(anchor.Name);
            var to = End(anchor.To!);
            if (from is null || to is null) return Make(anchor, null, false);
            bool touched;
            if (from.Node is not null && to.Node is not null)
            {
                touched = changedEdges.Any(e => Identifiers.IsUnder(e.From, from.Node) && Identifiers.IsUnder(e.To, to.Node));
            }
            else
            {
                touched = changedEdges.Any(e => (e.Evidence ?? []).Any(p => from.Matches(p.From, e.From) && to.Matches(p.To, e.To)));
            }
            return Make(anchor, $"{from.Target} → {to.Target}", touched, step => step.Kind switch
            {
                "edge" => step.Target.Split('→') is [var a, var b] && CoversEdge(from, to, a, b),
                "cycle" => (step.Members ?? []).Any(a => (step.Members ?? []).Any(b => a != b && CoversEdge(from, to, a, b))),
                _ => false,
            });
        }

        /// <summary>
        /// Gets a value indicating whether an edge anchor covers the drawn edge between two blocks: the edge itself, an
        /// edge between blocks inside its two ends, or a type pair in its evidence. A block is a node, or a group of a
        /// view as <see cref="DrawnEdges.GroupKey"/> names it.
        /// </summary>
        private bool CoversEdge(EdgeEnd from, EdgeEnd to, string fromBlock, string toBlock)
        {
            if (from.Node is not null && !Inside(from.Node, fromBlock) || to.Node is not null && !Inside(to.Node, toBlock)) return false;
            if (from.Node is not null && to.Node is not null) return true;
            return changedEdges.Any(e => Inside(e.From, fromBlock) && Inside(e.To, toBlock)
                && (e.Evidence ?? []).Any(p => from.Matches(p.From, e.From) && to.Matches(p.To, e.To)));
        }

        /// <summary>Gets a value indicating whether a node is a block or lies inside it. A node lies inside a group when it is one of the group's leaves or under one.</summary>
        private bool Inside(string node, string block)
        {
            if (!block.StartsWith("g:", StringComparison.Ordinal)) return Identifiers.IsUnder(node, block);
            return block[2..].Split('/') is [var view, var group] && changeset.Map.Views.TryGetValue(view, out var v) && v.Groups.TryGetValue(group, out var g)
                && g.Leaves.Any(l => Identifiers.IsUnder(node, l));
        }

        /// <summary>Returns the decision a step opens, or <see langword="null"/> for any other step.</summary>
        private Decision? DecisionOf(PathStep step) => step.Kind == "decision" ? changeset.Decisions.FirstOrDefault(d => d.Id == step.Target) : null;

        /// <summary>Represents a resolved end of an edge anchor: a node identifier, or the bare names of the types it names.</summary>
        private sealed record EdgeEnd(string? Node, IReadOnlySet<string> Types, string Target)
        {
            /// <summary>Gets a value indicating whether a type of a pair, behind an edge from or to a leaf, is at this end.</summary>
            public bool Matches(string type, string leaf) => Node is not null ? Identifiers.IsUnder(leaf, Node) : Types.Contains(Bare(type));
        }

        private EdgeEnd? End(string end)
        {
            if (Identifiers.IsValid(end)) return IsNode(end) ? new EdgeEnd(end, new HashSet<string>(), end) : null;
            var types = Types(end);
            return types.Count == 0 ? null : new EdgeEnd(null, types.Select(t => Bare(t.Name)).ToHashSet(StringComparer.Ordinal), types[0].Name);
        }

        /// <summary>
        /// Resolves a file anchor, given from the repository's top. A file is known when the changeset lists it or a type
        /// is declared in it, and touched when the change adds, removes, renames or modifies it.
        /// </summary>
        private ResolvedAnchor File(IntentAnchor anchor)
        {
            if (ReviewPipeline.ChangesetPath(anchor.Name, root) is not { } path) return Make(anchor, null, false);
            // A file covers the decision whose hunk it holds.
            bool Holds(Decision d) => d.File == path;
            if (files.TryGetValue(path, out var file)) return Make(anchor, path, file.Status != "unchanged", deciding: Holds);
            return Make(anchor, declared.Contains(path) ? path : null, false, deciding: Holds);
        }

        /// <summary>
        /// Resolves a type anchor, given by its full name or by a simple name that exactly one type in the changeset has.
        /// A type is touched when it was added, removed or moved, or a file that declares it changed.
        /// </summary>
        private ResolvedAnchor Type(IntentAnchor anchor)
        {
            var types = Types(anchor.Name);
            if (types.Count == 0) return Make(anchor, null, false);
            var touched = types.Any(t => t.Status != "unchanged" || t.Files.Any(f => files.TryGetValue(f, out var c) && c.Status != "unchanged"));
            // A type covers a decision that declares it or one of its members.
            var names = types.Select(t => Bare(t.Name)).ToHashSet(StringComparer.Ordinal);
            bool Declares(string? symbol) => symbol is not null && names.Any(n => symbol == n || symbol.StartsWith(n + ".", StringComparison.Ordinal));
            return Make(anchor, types[0].Name, touched, deciding: d => d.Declarations.Any(x => Declares(x.Symbol) || Declares(x.Previous)));
        }

        /// <summary>
        /// Returns the types a name names: every type with that full name, which is more than one only when assemblies
        /// share it, or else the one type with that simple name. A simple name that several types share names none.
        /// </summary>
        private List<TypeChange> Types(string name)
        {
            if (byName.TryGetValue(name, out var full)) return full;
            return !name.Contains('.') && bySimpleName.TryGetValue(name, out var simple) && simple.Count == 1 ? simple : [];
        }
    }
}
