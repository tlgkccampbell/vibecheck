using Vibecheck.Model;

namespace Vibecheck.Checking;

/// <summary>Answers why an edge exists: the pairs of types behind the references from one node to another.</summary>
public static class Explain
{
    /// <summary>
    /// Returns the type pairs whose references run from a type under <paramref name="from"/>
    /// to a type under <paramref name="to"/>, with counts, most frequent first. Either
    /// identifier may be a node at any level or the unsorted leaf, and the target may
    /// be an external. A pair inside one leaf is reported only when both identifiers
    /// name that leaf.
    /// </summary>
    public static List<TypePair> Pairs(Facts facts, string from, string to)
    {
        var leafOf = facts.Types.ToDictionary(t => t.Name, t => t.Leaf, StringComparer.Ordinal);
        var counts = new Dictionary<(string, string), int>();
        if (facts.Map.Externals.ContainsKey(to))
        {
            foreach (var e in facts.ExternalRefs)
            {
                if (e.External == to && leafOf.TryGetValue(e.From, out var leaf) && Identifiers.IsWithin(leaf, from))
                    counts[(e.From, e.Assembly)] = counts.GetValueOrDefault((e.From, e.Assembly)) + e.Count;
            }
        }
        else
        {
            foreach (var d in facts.Dependencies)
            {
                var a = leafOf[d.From];
                var b = leafOf[d.To];
                if (!Identifiers.IsWithin(a, from) || !Identifiers.IsWithin(b, to)) continue;
                if (from != to && a == b) continue;
                counts[(d.From, d.To)] = counts.GetValueOrDefault((d.From, d.To)) + 1;
            }
        }
        return counts.Select(kv => new TypePair(kv.Key.Item1, kv.Key.Item2, kv.Value))
            .OrderByDescending(p => p.Count).ThenBy(p => p.FromType, StringComparer.Ordinal).ThenBy(p => p.ToType, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>The references from one type to another, or to one external assembly, behind an edge.</summary>
public sealed record TypePair(string FromType, string ToType, int Count);
