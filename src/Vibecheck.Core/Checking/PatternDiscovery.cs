using Vibecheck.Analysis;
using Vibecheck.Model;

namespace Vibecheck.Checking;

/// <summary>
/// Finds the patterns among the types of a commit. A pattern is a set of keys, as <see cref="PatternKeys"/> defines
/// them, and the types that carry all of them, its instances. Discovery groups the types by each key they carry, keeps
/// each group of at least <see cref="MinimumInstances"/> types, and merges groups with the same instances into one
/// pattern. The map's patterns list names, declares and ignores patterns. See <c>docs/patterns.md</c> section 4.
/// </summary>
public static class PatternDiscovery
{
    /// <summary>The number of types that must share a key for discovery to find a pattern.</summary>
    public const int MinimumInstances = 3;

    /// <summary>The value of <see cref="PatternFact.By"/> for a pattern that an entry of the map names or declares.</summary>
    public const string ByMap = "map";

    /// <summary>The value of <see cref="PatternFact.By"/> for a pattern named from its first key.</summary>
    public const string ByKey = "key";

    /// <summary>Returns the keys that a type's traits give it, which are those discovery groups by. A suffix key is never among them.</summary>
    public static IEnumerable<string> KeysOf(TypeTraits? traits)
    {
        if (traits is null) yield break;
        if (traits.Base is { } b) yield return PatternKeys.Of(PatternKeys.Inherits, b.Type);
        foreach (var i in traits.Interfaces) yield return PatternKeys.Of(PatternKeys.Implements, i.Type);
        foreach (var a in traits.Attributes) yield return PatternKeys.Of(PatternKeys.Attribute, a);
    }

    /// <summary>Returns a value indicating whether a type carries a key, by its traits or, for a suffix key, by its name.</summary>
    public static bool Carries(TypeFact type, string key)
    {
        if (PatternKeys.KindOf(key) != PatternKeys.Suffix) return KeysOf(type.Traits).Contains(key, StringComparer.Ordinal);
        var plain = TypeNames.Plain(type.Name);
        return plain[(plain.LastIndexOf('.') + 1)..].EndsWith(PatternKeys.ValueOf(key), StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the types that can be instances of a pattern: every type of the system except one whose every file a
    /// generator wrote, since its shape is the generator's. The facts' types already leave out test types and excluded
    /// types, and nested types are never listed apart from the type that contains them.
    /// </summary>
    /// <param name="types">The types of the system.</param>
    /// <param name="generated">The paths of the files that a generator wrote.</param>
    public static List<TypeFact> Candidates(IEnumerable<TypeFact> types, IReadOnlySet<string> generated) =>
        types.Where(t => !t.Files.All(generated.Contains)).ToList();

    /// <summary>Returns the types, among some candidates, that carry every one of a pattern's keys.</summary>
    public static List<TypeFact> InstancesOf(IReadOnlyList<string> keys, IEnumerable<TypeFact> candidates) =>
        candidates.Where(t => keys.All(k => Carries(t, k))).ToList();

    /// <summary>Returns the patterns among the types of a commit, most instances first. Only the <see cref="Candidates"/> can be instances.</summary>
    /// <param name="map">The map whose patterns list names, declares and ignores patterns.</param>
    /// <param name="types">The types of the system.</param>
    /// <param name="generated">The paths of the files that a generator wrote.</param>
    /// <param name="warnings">The list that receives a warning for each entry whose keys no type carries, and for each ignored suffix key.</param>
    public static List<PatternFact> Discover(ArchitectureMap map, IReadOnlyList<TypeFact> types, IReadOnlySet<string> generated, List<string> warnings)
    {
        var candidates = Candidates(types, generated);
        var ignored = map.Patterns.Select(p => p.Ignore).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var key in ignored.Where(k => PatternKeys.KindOf(k) == PatternKeys.Suffix))
            warnings.Add($"patterns: discovery never finds a suffix key, so ignoring \"{key}\" drops nothing");

        var groups = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var type in candidates)
        {
            foreach (var key in KeysOf(type.Traits).Where(k => !ignored.Contains(k)))
            {
                if (!groups.TryGetValue(key, out var set)) groups[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(type.Name);
            }
        }
        var discovered = groups.Where(g => g.Value.Count >= MinimumInstances)
            .GroupBy(g => string.Join("\n", g.Value), StringComparer.Ordinal)
            .Select(g => (Keys: Ordered(g.Select(x => x.Key)), Instances: g.First().Value.ToList()))
            .ToList();

        var result = new List<PatternFact>();
        foreach (var entry in map.Patterns.Where(p => p.Keys is not null))
        {
            var instances = InstancesOf(entry.Keys!, candidates).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
            if (instances.Count == 0) warnings.Add($"patterns: no type carries every key of {entry.Id} ({string.Join(", ", entry.Keys!)})");
            // An entry names the pattern discovery found with the same instances, which may carry more keys than the
            // entry lists, since keys whose groups have the same instances are merged.
            var match = discovered.FindIndex(d => d.Instances.SequenceEqual(instances, StringComparer.Ordinal) && entry.Keys!.All(d.Keys.Contains));
            var keys = match >= 0 ? discovered[match].Keys : Ordered(entry.Keys!);
            if (match >= 0) discovered.RemoveAt(match);
            result.Add(new PatternFact(entry.Id!, entry.Name ?? PatternKeys.NameOf(keys[0]), entry.Purpose, ByMap, keys, instances));
        }
        result.AddRange(discovered.Select(d => new PatternFact(string.Join(" + ", d.Keys), PatternKeys.NameOf(d.Keys[0]), null, ByKey, d.Keys, d.Instances)));
        return result.OrderByDescending(p => p.Instances.Count).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns the patterns, among those a type is an instance of, under which the review reports the type. A pattern
    /// whose instances include every instance of another of them and more is left out, so that a type is reported under
    /// the most specific pattern while the broader one still counts it.
    /// </summary>
    public static List<PatternFact> ReportedUnder(IReadOnlyList<PatternFact> patterns)
    {
        var sets = patterns.Select(p => p.Instances.ToHashSet(StringComparer.Ordinal)).ToList();
        return patterns.Where((_, i) => !sets.Where((_, j) => j != i).Any(s => s.IsProperSubsetOf(sets[i]))).ToList();
    }

    /// <summary>
    /// Returns a line for each pattern, as <c>vibecheck check</c> reports them, that names it and gives its number of
    /// instances, its number of norms, the mean number of norms its own instances lack, and its keys.
    /// </summary>
    public static IEnumerable<string> Report(IEnumerable<PatternFact> patterns) =>
        patterns.Select(p => $"pattern {p.Name}: {p.Instances.Count} instance{(p.Instances.Count == 1 ? "" : "s")}, {p.Norms.Count} norm{(p.Norms.Count == 1 ? "" : "s")}, "
            + $"{p.MeanDepartures.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)} departures each ({string.Join(", ", p.Keys)})");

    private static List<string> Ordered(IEnumerable<string> keys) =>
        keys.Distinct(StringComparer.Ordinal).OrderBy(PatternKeys.Rank).ThenBy(k => k, StringComparer.Ordinal).ToList();
}
