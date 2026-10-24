using Vibecheck.Analysis;
using Vibecheck.Checking;
using Vibecheck.Model;

namespace Vibecheck.Diffing;

/// <summary>
/// One name a changeset introduces, as <c>docs/attention.md</c> section 8 describes it: a type or member added, a
/// declaration renamed, a node, view or external added to the map, or an assembly referenced for the first time. The
/// key files the term wherever one thing per term is kept, such as Claude's definition of it. The kind is the
/// declaration's kind for a type or member, or <see cref="Node"/>, <see cref="View"/>, <see cref="External"/> or
/// <see cref="Assembly"/>. The block is the leaf that holds the term at head, and null for a term the map holds
/// rather than a block. The uses are the sites that reference the term at head.
/// </summary>
public sealed record Term(string Key, string Name, string Symbol, string Kind, string? Block, int Uses)
{
    /// <summary>The kind of a node added to the map.</summary>
    public const string Node = "node";

    /// <summary>The kind of a view added to the map.</summary>
    public const string View = "view";

    /// <summary>The kind of an external added to the map.</summary>
    public const string External = "external";

    /// <summary>The kind of an assembly that head references and base did not.</summary>
    public const string Assembly = "assembly";

    /// <summary>Gets the symbol a renamed declaration had at base, or <see langword="null"/> for any other term.</summary>
    public string? Previous { get; init; }

    /// <summary>Gets a declaration's accessibility as C# writes it, or <see langword="null"/> for a term that is not a declaration.</summary>
    public string? Accessibility { get; init; }

    /// <summary>Gets a value indicating whether code outside the assembly can see the declaration.</summary>
    public bool Exposed { get; init; }

    /// <summary>Gets the file that declares the term at head, or <see langword="null"/> for a term the map or an assembly introduces.</summary>
    public string? File { get; init; }

    /// <summary>Gets the one-based line the declaration starts on, or <see langword="null"/> for a term with no file.</summary>
    public int? Line { get; init; }

    /// <summary>Gets the first sentence of the declaration's documentation summary, or a map part's purpose, or <see langword="null"/> when there is none.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the first site outside the declaring type that uses the term at head, by file and line, or <see langword="null"/> when none does.</summary>
    public SiteRef? FirstUse { get; init; }

    /// <summary>Gets the identifier of the decision that introduced the term, or <see langword="null"/> when no decision holds its declaration.</summary>
    public string? Decision { get; init; }

    /// <summary>Returns the key of a declaration's term, such as <c>term:A.Clock.Now</c>.</summary>
    public static string KeyOf(string symbol) => "term:" + symbol;

    /// <summary>Returns a value indicating whether a kind is a declaration's rather than a map part's or an assembly's.</summary>
    public static bool IsDeclaration(string kind) => kind is not (Node or View or External or Assembly);
}

/// <summary>A place in a file at head, by its path and one-based line.</summary>
public sealed record SiteRef(string File, int Line);

/// <summary>
/// Lists the vocabulary a changeset introduces, as <c>docs/attention.md</c> section 8 describes it, from the
/// declaration diff and the map diff. Each added or renamed name is listed once: a renamed type is one term, and its
/// members are listed again only when their own names changed. Constructors are not terms, since a type's name covers
/// them. The list is grouped by block, in the order of their identifiers, and within a block the most used terms come
/// first, so that the names a reviewer will meet most come first.
/// </summary>
public static class Glossary
{
    /// <summary>Returns the terms of a changeset, grouped by block and ordered by use.</summary>
    /// <param name="cs">The changeset, with its decisions and its structural diff.</param>
    /// <param name="baseFacts">The facts at base, whose external references tell which assemblies are new at head.</param>
    /// <param name="headFacts">The facts at head, whose declarations, sites and types give each term its place and its uses.</param>
    /// <param name="changes">The declaration diff of the two ends.</param>
    public static List<Term> Build(Changeset cs, Facts baseFacts, Facts headFacts, IReadOnlyList<DeclarationChange> changes)
    {
        var terms = new List<Term>();
        var declared = headFacts.Declarations.GroupBy(d => d.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.FirstLine).ToList(), StringComparer.Ordinal);
        var leafOf = headFacts.Types.ToDictionary(t => t.Name, t => t.Leaf, StringComparer.Ordinal);
        var sites = new Dictionary<string, List<(string From, string File, int Line)>>(StringComparer.Ordinal);
        void AddSite(string symbol, string from, string file, int line)
        {
            if (!sites.TryGetValue(symbol, out var list)) sites[symbol] = list = [];
            list.Add((from, file, line));
        }
        foreach (var d in headFacts.Dependencies) AddSite(d.Symbol, d.From, d.File, d.Line);
        foreach (var s in headFacts.Sites) AddSite(s.Symbol, s.FromType, s.File, s.Line);
        var decisionOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in cs.Decisions) foreach (var x in d.Declarations) decisionOf.TryAdd(x.Symbol, d.Id);

        foreach (var c in changes)
        {
            if (c.Kind == "constructor" || !declared.TryGetValue(c.Symbol, out var parts)) continue;
            if (c.Change == "renamed" && c.Previous is not null && DeclarationDiff.SimpleName(c.Previous) == DeclarationDiff.SimpleName(c.Symbol)) continue;
            if (c.Change is not ("added" or "renamed")) continue;
            var first = parts[0];
            var all = sites.GetValueOrDefault(c.Symbol, []);
            var outside = all.Where(s => s.From != first.Type).OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).FirstOrDefault();
            terms.Add(new Term(Term.KeyOf(c.Symbol), DeclarationDiff.ShortName(c.Symbol, c.Kind), c.Symbol, c.Kind, leafOf.GetValueOrDefault(first.Type), all.Count)
            {
                Previous = c.Previous,
                Accessibility = first.Accessibility,
                Exposed = first.Exposed,
                File = first.File,
                Line = first.FirstLine,
                Summary = parts.Select(p => p.Summary).FirstOrDefault(s => s is not null),
                FirstUse = outside.File is null ? null : new SiteRef(outside.File, outside.Line),
                Decision = decisionOf.GetValueOrDefault(c.Symbol),
            });
        }

        var map = cs.Map;
        foreach (var n in cs.Structural.Nodes.Where(n => n.Op == "added"))
        {
            if (n.Kind == "external" && map.Externals.TryGetValue(n.Id, out var x))
                terms.Add(new Term("term:external:" + n.Id, x.Name, n.Id, Term.External, null, headFacts.ExternalRefs.Where(r => r.External == n.Id).Sum(r => r.Count)) { Summary = x.Purpose });
            else if (map.Nodes.TryGetValue(n.Id, out var node))
                terms.Add(new Term("term:node:" + n.Id, node.Name, n.Id, Term.Node, n.Id, 0) { Summary = node.Purpose });
        }
        foreach (var v in cs.Structural.Views.Where(v => v.Op == "added"))
            if (map.Views.TryGetValue(v.Id, out var view)) terms.Add(new Term("term:view:" + v.Id, view.Name, v.Id, Term.View, null, 0) { Summary = view.Purpose });

        var before = baseFacts.ExternalRefs.Select(r => r.Assembly).ToHashSet(StringComparer.Ordinal);
        foreach (var g in headFacts.ExternalRefs.Where(r => !before.Contains(r.Assembly)).GroupBy(r => r.Assembly, StringComparer.Ordinal))
        {
            var use = g.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).First();
            terms.Add(new Term("term:assembly:" + g.Key, g.Key, g.Key, Term.Assembly, null, g.Sum(r => r.Count)) { FirstUse = new SiteRef(use.File, use.Line) });
        }

        return terms.OrderBy(t => t.Block is null ? 1 : 0).ThenBy(t => t.Block, StringComparer.Ordinal)
            .ThenByDescending(t => t.Uses).ThenByDescending(t => t.Exposed).ThenBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
    }
}
