using System.Text;
using Vibecheck.Analysis;

namespace Vibecheck.Bootstrap;

/// <summary>
/// A type together with the types that belong to it on structural grounds alone, as
/// <c>docs/vibecheck-spec.md</c> section 5.2 defines it. The head is the type the others
/// folded into.
/// </summary>
public sealed record Unit(
    string Head,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Namespaces,
    string? Summary,
    IReadOnlyList<(string Unit, int Count)> References,
    IReadOnlyList<(string Unit, int Count)> ReferencedBy);

/// <summary>
/// Folds an analysis into units for the proposing step of bootstrapping, and writes
/// the listing a language model reads to propose a map.
/// </summary>
public static class Units
{
    /// <summary>A helper references at most this many other units of its own. Beyond it a type is a consumer, and consumers do not fold into their callers.</summary>
    public const int MaxHelperReferences = 2;

    public static List<Unit> Build(AnalysisResult analysis)
    {
        var types = analysis.Types.Select(t => t.Name).ToList();
        var known = types.ToHashSet(StringComparer.Ordinal);
        var byName = analysis.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var references = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in analysis.Dependencies)
        {
            if (d.FromType == d.ToType || !known.Contains(d.FromType) || !known.Contains(d.ToType)) continue;
            if (!references.TryGetValue(d.FromType, out var tos)) references[d.FromType] = tos = new Dictionary<string, int>(StringComparer.Ordinal);
            tos[d.ToType] = tos.GetValueOrDefault(d.ToType) + 1;
            if (d.Kind == "inherits") bases[d.FromType] = d.ToType;
        }

        var parent = types.ToDictionary(t => t, t => t, StringComparer.Ordinal);
        string Find(string x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(string child, string head)
        {
            var a = Find(child);
            var b = Find(head);
            if (a != b) parent[a] = b;
        }

        // A subclass joins its base when they share a namespace. A base whose subclasses
        // live elsewhere is an extension point, and each subclass belongs to its own concept.
        foreach (var (sub, baseType) in bases)
        {
            if (byName[sub].Namespace == byName[baseType].Namespace) Union(sub, baseType);
        }

        // A helper joins its only user, repeatedly. A helper references few other units
        // itself; a type with many references is a consumer, and folding consumers into
        // their callers would collapse a pipeline into its driver.
        while (true)
        {
            var dependents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var outDegree = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (from, tos) in references)
            {
                var uf = Find(from);
                foreach (var to in tos.Keys)
                {
                    var ut = Find(to);
                    if (uf == ut) continue;
                    if (!dependents.TryGetValue(ut, out var set)) dependents[ut] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(uf);
                    if (!outDegree.TryGetValue(uf, out var outs)) outDegree[uf] = outs = new HashSet<string>(StringComparer.Ordinal);
                    outs.Add(ut);
                }
            }
            var changed = false;
            foreach (var (unit, users) in dependents.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (users.Count != 1 || Find(unit) != unit) continue;
                if ((outDegree.GetValueOrDefault(unit)?.Count ?? 0) > MaxHelperReferences) continue;
                var user = users.First();
                if (Find(user) == unit) continue;
                Union(unit, user);
                changed = true;
            }
            if (!changed) break;
        }

        var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in types)
        {
            var head = Find(t);
            if (!members.TryGetValue(head, out var list)) members[head] = list = [];
            list.Add(t);
        }
        var unitRefs = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var unitIn = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (from, tos) in references)
        {
            var uf = Find(from);
            foreach (var (to, n) in tos)
            {
                var ut = Find(to);
                if (uf == ut) continue;
                if (!unitRefs.TryGetValue(uf, out var outs)) unitRefs[uf] = outs = new Dictionary<string, int>(StringComparer.Ordinal);
                outs[ut] = outs.GetValueOrDefault(ut) + n;
                if (!unitIn.TryGetValue(ut, out var ins)) unitIn[ut] = ins = new Dictionary<string, int>(StringComparer.Ordinal);
                ins[uf] = ins.GetValueOrDefault(uf) + n;
            }
        }
        static List<(string, int)> Sorted(Dictionary<string, int>? d) =>
            (d ?? []).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();

        return members
            .Select(kv => new Unit(kv.Key, kv.Value.OrderBy(m => m, StringComparer.Ordinal).ToList(),
                kv.Value.Select(m => byName[m].Namespace).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(),
                byName[kv.Key].Summary, Sorted(unitRefs.GetValueOrDefault(kv.Key)), Sorted(unitIn.GetValueOrDefault(kv.Key))))
            .OrderBy(u => u.Namespaces[0], StringComparer.Ordinal).ThenByDescending(u => u.Members.Count).ThenBy(u => u.Head, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Writes the listing of section 5.2: units grouped by namespace, each with its summary, members and heaviest references.</summary>
    public static string Listing(List<Unit> units, int typeCount)
    {
        var sb = new StringBuilder();
        var folded = units.Sum(u => u.Members.Count - 1);
        sb.Append($"{typeCount} types in {units.Count} units ({folded} folded into their user or base)\n");
        string? current = null;
        foreach (var u in units)
        {
            var ns = u.Namespaces[0];
            if (ns != current)
            {
                current = ns;
                sb.Append($"\n=== {(ns.Length == 0 ? "(global)" : ns)} ===\n");
            }
            sb.Append('\n').Append(Short(u.Head)).Append("  [").Append(u.Members.Count).Append(']');
            if (u.Namespaces.Count > 1) sb.Append("  spans ").Append(string.Join(", ", u.Namespaces));
            sb.Append('\n');
            if (u.Summary is not null) sb.Append("  \"").Append(u.Summary).Append("\"\n");
            if (u.Members.Count > 1)
            {
                var others = u.Members.Where(m => m != u.Head).Select(Short).ToList();
                sb.Append("  + ").Append(string.Join(", ", others.Take(40))).Append(others.Count > 40 ? " …" : "").Append('\n');
            }
            if (u.References.Count > 0) sb.Append("  -> ").Append(string.Join(", ", u.References.Take(6).Select(r => $"{Short(r.Unit)}:{r.Count}"))).Append('\n');
            if (u.ReferencedBy.Count > 0) sb.Append("  <- ").Append(string.Join(", ", u.ReferencedBy.Take(6).Select(r => $"{Short(r.Unit)}:{r.Count}"))).Append('\n');
        }
        return sb.ToString();
    }

    private static string Short(string name)
    {
        var plain = TypeNames.Plain(name);
        var i = plain.LastIndexOf('.');
        return i < 0 ? plain : plain[(i + 1)..];
    }
}
