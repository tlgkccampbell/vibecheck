using Vibecheck.Analysis;
using Vibecheck.Checking;

namespace Vibecheck.Diffing;

/// <summary>
/// Chooses the examples of the instances of a pattern, carries their source, and aligns an instance with another
/// member by member, for the compare view. See <c>docs/patterns.md</c> sections 6 and 7.3.
/// </summary>
public static class PatternExamples
{
    /// <summary>The most lines of a type's source that the changeset carries.</summary>
    public const int MostLines = 300;

    /// <summary>
    /// Returns how alike two instances are: the share of their combined features that both have, or 1 when neither has
    /// any feature.
    /// </summary>
    public static double Likeness(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        var union = a.Count + b.Count - a.Count(b.Contains);
        return union == 0 ? 1 : (double)a.Count(b.Contains) / union;
    }

    /// <summary>
    /// Returns the nearest and the typical example of each of some instances. The examples are chosen among the
    /// pattern's instances at head that the change did not add, so that an example is code that existed before. The
    /// nearest has the highest <see cref="Likeness"/> to the instance, ties going to one in the same leaf, then to one
    /// whose key type arguments share more leading namespace segments with the instance's, then to the first by name.
    /// The typical has the highest total likeness to the other candidates, and is the same for every instance unless it
    /// is the instance or its nearest, when the next in that order takes its place.
    /// </summary>
    /// <param name="instances">The instances to choose examples for.</param>
    /// <param name="all">Every instance of the pattern at head.</param>
    /// <param name="added">The names of the types the change added.</param>
    /// <param name="features">The features of each instance at head, by its name.</param>
    /// <param name="arguments">The key type arguments of each instance at head, by its name.</param>
    public static Dictionary<string, (string? Nearest, string? Typical)> Choose(IEnumerable<TypeFact> instances, IReadOnlyList<TypeFact> all, IReadOnlySet<string> added,
        IReadOnlyDictionary<string, SortedSet<string>> features, IReadOnlyDictionary<string, List<string>> arguments)
    {
        var candidates = all.Where(t => !added.Contains(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var typical = Typical(candidates.Select(c => c.Name), features);
        var result = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        foreach (var x in instances)
        {
            var nearest = candidates.Where(c => c.Name != x.Name)
                .OrderByDescending(c => Likeness(features[x.Name], features[c.Name]))
                .ThenByDescending(c => c.Leaf == x.Leaf)
                .ThenByDescending(c => SharedNamespace(arguments[x.Name], arguments[c.Name]))
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => c.Name).FirstOrDefault();
            result[x.Name] = (nearest, typical.FirstOrDefault(n => n != x.Name && n != nearest));
        }
        return result;
    }

    /// <summary>
    /// Returns some instances with the most typical first: the one with the highest total <see cref="Likeness"/> to the
    /// others, ties going to the first by name.
    /// </summary>
    /// <param name="names">The instances' names.</param>
    /// <param name="features">The features of each instance, by its name.</param>
    public static List<string> Typical(IEnumerable<string> names, IReadOnlyDictionary<string, SortedSet<string>> features)
    {
        var all = names.Order(StringComparer.Ordinal).ToList();
        return all.Select(c => (Name: c, Total: all.Where(d => d != c).Sum(d => Likeness(features[c], features[d]))))
            .OrderByDescending(x => x.Total).ThenBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name).ToList();
    }

    /// <summary>Returns the most leading namespace segments that a type argument of one list shares with one of another.</summary>
    private static int SharedNamespace(List<string> a, List<string> b)
    {
        static string[] Namespace(string type) => type.Split('.')[..^1];
        return a.SelectMany(x => b.Select(y => Namespace(x).Zip(Namespace(y)).TakeWhile(p => p.First == p.Second).Count())).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// The outline of a type in one of the files that declare it: its declaration and its direct members, each with its
    /// lines, and the source those lines come from.
    /// </summary>
    /// <param name="Declaration">The type's declaration.</param>
    /// <param name="Members">The type's direct members in the file, in source order, those cut from the source left out.</param>
    /// <param name="Source">The type's source.</param>
    public sealed record Outline(Declaration Declaration, List<Declaration> Members, TypeSource Source);

    /// <summary>
    /// Returns the outline of a type at head, from the file that declares most of it, or <see langword="null"/> when the
    /// type has no declaration or its file's text is not given. A type longer than <see cref="MostLines"/> is cut at its
    /// last whole member within the limit.
    /// </summary>
    /// <param name="type">The type's name.</param>
    /// <param name="declarations">The declarations at head.</param>
    /// <param name="texts">The texts of files at head, by path.</param>
    public static Outline? OutlineOf(string type, IReadOnlyList<Declaration> declarations, IReadOnlyDictionary<string, string> texts)
    {
        var decl = TypeLocations.MainPart(declarations.Where(d => d.Type == type && d.Kind == "type" && d.Container is null));
        if (decl is null || !texts.TryGetValue(decl.File, out var text)) return null;
        var members = declarations.Where(d => d.Type == type && d.Container == decl.Symbol && d.File == decl.File && d.FirstLine >= decl.FirstLine && d.LastLine <= decl.LastLine)
            .OrderBy(d => d.FirstLine).ToList();
        var last = decl.LastLine;
        var cut = last - decl.FirstLine + 1 > MostLines;
        if (cut)
        {
            members = members.Where(m => m.LastLine < decl.FirstLine + MostLines).ToList();
            last = members.Count > 0 ? members[^1].LastLine : decl.FirstLine + MostLines - 1;
        }
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        var source = new TypeSource(decl.File, decl.FirstLine, lines[(decl.FirstLine - 1)..Math.Min(last, lines.Length)].ToList(), cut);
        return new Outline(decl, members, source);
    }

    /// <summary>
    /// Returns the rows that align one type with another, member by member. The declarations share the first row, the
    /// constructors are paired in order, then the members with the same name, then those with the same shape, which is
    /// a member's declaration without its name and body. A member with no counterpart has a row of its own, placed after
    /// the row of the member before it.
    /// </summary>
    public static List<CompareRow> Align(Outline left, Outline right)
    {
        var pairs = new Dictionary<Declaration, Declaration>();
        void Pair(Func<Declaration, bool> which, Func<Declaration, string> by)
        {
            var free = right.Members.Where(r => which(r) && !pairs.ContainsValue(r)).ToList();
            foreach (var l in left.Members.Where(l => which(l) && !pairs.ContainsKey(l)))
            {
                var match = free.FirstOrDefault(r => by(r) == by(l));
                if (match is null) continue;
                pairs[l] = match;
                free.Remove(match);
            }
        }
        Pair(d => d.Kind == "constructor", _ => "");
        Pair(d => d.Kind != "constructor", d => d.Kind + " " + d.Name);
        Pair(d => d.Kind != "constructor", d => d.Kind + " " + d.Shape);

        static LineRange Header(Declaration d) => new(d.FirstLine, d.BodyLine is { } b && b >= d.FirstLine ? b : d.LastLine);
        static LineRange Lines(Declaration d) => new(d.FirstLine, d.LastLine);
        var rows = new List<CompareRow> { new(left.Declaration.Name, Header(left.Declaration), right.Declaration.Name, Header(right.Declaration)) };
        foreach (var l in left.Members)
            rows.Add(pairs.TryGetValue(l, out var r) ? new CompareRow(l.Name, Lines(l), r.Name, Lines(r)) : new CompareRow(l.Name, Lines(l), null, null));
        var paired = pairs.Values.ToHashSet();
        for (var i = 0; i < right.Members.Count; i++)
        {
            var r = right.Members[i];
            if (paired.Contains(r)) continue;
            // The row goes after the row that shows the member before it on the right, or after the declaration.
            var after = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var k = rows.FindIndex(x => x.RightLines is { } lines && lines.First == right.Members[j].FirstLine);
                if (k >= 0) { after = k; break; }
            }
            rows.Insert(after + 1, new CompareRow(null, null, r.Name, Lines(r)));
        }
        return rows;
    }

    /// <summary>
    /// Returns the rows of <see cref="Align"/> with an instance's departures and unusual traits marked on the rows that
    /// show them: a missing injected type on the first constructor's row, and everything else on the declaration's row.
    /// </summary>
    /// <param name="rows">The rows, whose left side is the instance.</param>
    /// <param name="left">The instance's outline.</param>
    /// <param name="departures">The words for each departure, by its feature.</param>
    /// <param name="unusual">The words for each unusual trait.</param>
    public static List<CompareRow> Mark(List<CompareRow> rows, Outline left, IReadOnlyList<(string Feature, string Text)> departures, IEnumerable<string> unusual)
    {
        var constructor = left.Members.FirstOrDefault(m => m.Kind == "constructor");
        var at = constructor is null ? 0 : rows.FindIndex(r => r.LeftLines is { } lines && lines.First == constructor.FirstLine);
        var marks = rows.Select(_ => new List<string>()).ToList();
        foreach (var (feature, text) in departures) marks[feature.StartsWith(PatternFeatures.Injects, StringComparison.Ordinal) && at >= 0 ? at : 0].Add(text);
        marks[0].AddRange(unusual);
        return rows.Select((r, i) => marks[i].Count == 0 ? r : r with { Marks = marks[i] }).ToList();
    }
}
