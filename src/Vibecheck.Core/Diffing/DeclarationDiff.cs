using System.Security.Cryptography;
using System.Text;
using Vibecheck.Analysis;

namespace Vibecheck.Diffing;

/// <summary>
/// A declaration that differs between base and head. The change is <c>added</c>, <c>removed</c>, <c>reshaped</c> when
/// its shape differs, <c>rewritten</c> when only its body differs, <c>renamed</c> when a removed and an added
/// declaration have the same shape and body under different names, and <c>moved</c> when they have the same name in
/// different types or namespaces. A renamed or moved declaration names its symbol at base as its previous symbol.
/// </summary>
public sealed record DeclarationChange(string Symbol, string Change, string Kind, string? Previous = null)
{
    /// <summary>Gets the symbol at base, or <see langword="null"/> for an added declaration.</summary>
    public string? BaseSymbol => Change == "added" ? null : Previous ?? Symbol;

    /// <summary>Gets the symbol at head, or <see langword="null"/> for a removed declaration.</summary>
    public string? HeadSymbol => Change == "removed" ? null : Symbol;
}

/// <summary>
/// Matches the declarations of two ends by symbol and says how each one changed, as <c>docs/attention.md</c>
/// section 3 describes. Overloads and the parts of a partial type share a symbol, so they are one declaration whose
/// shapes and bodies are combined. Nothing in the result is interpreted.
/// </summary>
public static class DeclarationDiff
{
    /// <summary>One symbol at one end, with its occurrences' shapes and bodies combined.</summary>
    private sealed record Entry(string Symbol, string Kind, string? Container, string Shape, string? Body)
    {
        public string Simple => SimpleName(Container is null ? Symbol : Symbol[(Container.Length + 1)..]);
    }

    /// <summary>Returns every declaration that differs between base and head, ordered by symbol.</summary>
    public static List<DeclarationChange> Diff(IReadOnlyList<Declaration> baseDeclarations, IReadOnlyList<Declaration> headDeclarations)
    {
        var before = Entries(baseDeclarations);
        var after = Entries(headDeclarations);
        var result = new List<DeclarationChange>();
        foreach (var (symbol, b) in before)
        {
            if (!after.TryGetValue(symbol, out var h)) continue;
            if (b.Shape != h.Shape) result.Add(new DeclarationChange(symbol, "reshaped", h.Kind));
            else if (b.Body != h.Body) result.Add(new DeclarationChange(symbol, "rewritten", h.Kind));
        }
        var removed = before.Values.Where(e => !after.ContainsKey(e.Symbol)).ToDictionary(e => e.Symbol, StringComparer.Ordinal);
        var added = after.Values.Where(e => !before.ContainsKey(e.Symbol)).ToDictionary(e => e.Symbol, StringComparer.Ordinal);

        void Pair(Entry from, Entry to, string change)
        {
            result.Add(new DeclarationChange(to.Symbol, change, to.Kind, from.Symbol));
            removed.Remove(from.Symbol);
            added.Remove(to.Symbol);
        }

        // A type pairs with one that appeared when their own shapes and bodies match and so do all their members, which
        // then pair with it. Outer types go first, so that a nested type pairs as a member of the type around it.
        foreach (var from in removed.Values.Where(e => e.Kind == "type").OrderBy(e => e.Symbol.Length).ToList())
        {
            if (!removed.ContainsKey(from.Symbol)) continue;
            var fromMembers = MembersOf(removed, from.Symbol);
            var candidates = added.Values.Where(to => to.Kind == "type" && to.Shape == from.Shape && to.Body == from.Body
                && SameMembers(fromMembers, MembersOf(added, to.Symbol))).ToList();
            if (candidates.Count != 1) continue;
            var target = candidates[0];
            var change = target.Simple == from.Simple ? "moved" : "renamed";
            var toMembers = MembersOf(added, target.Symbol);
            Pair(from, target, change);
            foreach (var (rest, member) in fromMembers) Pair(member, toMembers[rest], change);
        }

        // A member pairs with one that appeared in the same type with the same shape and body when each is the only
        // match of the other, and with one of the same name in another type in the same way.
        PairUnique((a, b) => a.Container == b.Container, "renamed");
        PairUnique((a, b) => a.Simple == b.Simple && a.Container != b.Container, "moved");

        void PairUnique(Func<Entry, Entry, bool> related, string change)
        {
            bool Matches(Entry a, Entry b) => a.Kind != "type" && a.Kind == b.Kind && a.Shape == b.Shape && a.Body == b.Body && related(a, b);
            foreach (var from in removed.Values.ToList())
            {
                var candidates = added.Values.Where(to => Matches(from, to)).ToList();
                if (candidates.Count != 1) continue;
                if (removed.Values.Count(other => Matches(other, candidates[0])) != 1) continue;
                Pair(from, candidates[0], change);
            }
        }

        result.AddRange(removed.Values.Select(e => new DeclarationChange(e.Symbol, "removed", e.Kind)));
        result.AddRange(added.Values.Select(e => new DeclarationChange(e.Symbol, "added", e.Kind)));
        return result.OrderBy(c => c.Symbol, StringComparer.Ordinal).ThenBy(c => c.Change, StringComparer.Ordinal).ToList();
    }

    /// <summary>Groups an end's declarations by symbol, combining the shapes and bodies of overloads and partial parts.</summary>
    private static Dictionary<string, Entry> Entries(IReadOnlyList<Declaration> declarations) =>
        declarations.GroupBy(d => d.Symbol, StringComparer.Ordinal).ToDictionary(g => g.Key, g =>
        {
            var first = g.First();
            if (g.Count() == 1) return new Entry(g.Key, first.Kind, first.Container, first.Shape, first.Body);
            var shape = Combine(g.Select(d => d.Shape));
            var body = g.All(d => d.Body is null) ? null : Combine(g.Select(d => d.Body ?? ""));
            return new Entry(g.Key, first.Kind, first.Container, shape, body);
        }, StringComparer.Ordinal);

    private static string Combine(IEnumerable<string> hashes) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(' ', hashes.Order(StringComparer.Ordinal)))))[..16];

    /// <summary>Returns the entries declared inside a type, at any depth, keyed by their symbols without the type's.</summary>
    private static Dictionary<string, Entry> MembersOf(Dictionary<string, Entry> entries, string type) =>
        entries.Values.Where(e => e.Symbol.Length > type.Length + 1 && e.Symbol.StartsWith(type + ".", StringComparison.Ordinal))
            .ToDictionary(e => e.Symbol[(type.Length + 1)..], StringComparer.Ordinal);

    private static bool SameMembers(Dictionary<string, Entry> a, Dictionary<string, Entry> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other.Kind == kv.Value.Kind && other.Shape == kv.Value.Shape && other.Body == kv.Value.Body);

    /// <summary>Returns the name a declaration is shown by: a type's own name, or a member's containing type and name, without the namespace.</summary>
    public static string ShortName(string symbol, string kind)
    {
        var name = SimpleName(symbol);
        if (kind == "type") return name;
        var container = symbol.Length > name.Length + 1 ? symbol[..^(name.Length + 1)] : "";
        return container.Length > 0 ? SimpleName(container) + "." + name : name;
    }

    /// <summary>Returns the last dotted segment of a name, ignoring dots inside type arguments.</summary>
    internal static string SimpleName(string name)
    {
        var depth = 0;
        for (var i = name.Length - 1; i >= 0; i--)
        {
            if (name[i] == '>') depth++;
            else if (name[i] == '<') depth--;
            else if (name[i] == '.' && depth == 0) return name[(i + 1)..];
        }
        return name;
    }
}
