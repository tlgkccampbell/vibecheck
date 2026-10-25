using Vibecheck.Checking;

namespace Vibecheck.Diffing;

/// <summary>
/// Lists the patterns a change touches. A pattern is found at base, since the change is judged against the code it
/// joins, and a type at head is an instance when it carries every key of the pattern. See <c>docs/patterns.md</c>
/// sections 4.2 and 5.7.
/// </summary>
public static class PatternChanges
{
    /// <summary>
    /// Returns each pattern found at base that reports an instance the change added or changed, most such instances
    /// first. An instance is added when the type is new at head, and changed when it binds to another leaf or one of its
    /// files changed. A type is reported under the patterns that <see cref="PatternDiscovery.ReportedUnder"/> chooses,
    /// with its departures from the pattern's norms, its unusual traits, its companions and its examples. When the texts
    /// of files at head can be read, the pattern carries the source of its reported instances and their examples, and
    /// each reported instance the rows that align it with each of the others.
    /// </summary>
    /// <param name="baseFacts">The facts at base, which hold the patterns.</param>
    /// <param name="headFacts">The facts at head, whose types are the instances.</param>
    /// <param name="files">Every file of the changeset, with its status.</param>
    /// <param name="read">Returns the texts at head of the files at some paths, leaving out those it cannot read, or is <see langword="null"/> when no text can be read.</param>
    /// <summary>The number of instances at base that Claude names a pattern from, as <see cref="PatternChange.Samples"/> holds them.</summary>
    public const int Samples = 3;

    /// <summary>
    /// Returns the patterns new in a change, as <see cref="PatternDiscovery.New"/> finds them among the candidates at
    /// head that are not types at base, each with its instances and their leaves at head.
    /// </summary>
    /// <param name="baseFacts">The facts at base, which hold the patterns found there.</param>
    /// <param name="headFacts">The facts at head, whose map ignores keys and whose types are the instances.</param>
    public static List<NewPattern> New(Facts baseFacts, Facts headFacts)
    {
        var atBase = baseFacts.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var generated = headFacts.Files.Where(f => f.Generator is not null).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var added = PatternDiscovery.Candidates(headFacts.Types, generated).Where(t => !atBase.Contains(t.Name)).ToList();
        var leafOf = added.ToDictionary(t => t.Name, t => t.Leaf, StringComparer.Ordinal);
        return PatternDiscovery.New(headFacts.Map, added, baseFacts.Types, baseFacts.Patterns)
            .Select(p => new NewPattern(p.Id, p.Name, p.Keys, p.Instances.Select(n => new PatternOther(n, leafOf[n])).ToList())).ToList();
    }

    public static List<PatternChange> Build(Facts baseFacts, Facts headFacts, IReadOnlyList<FileChange> files,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, string>>? read = null)
    {
        if (baseFacts.Patterns.Count == 0) return [];
        var leafAtBase = baseFacts.Types.ToDictionary(t => t.Name, t => t.Leaf, StringComparer.Ordinal);
        var changed = files.Where(f => f.Status != "unchanged").Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        string? StatusOf(TypeFact t) =>
            !leafAtBase.TryGetValue(t.Name, out var leaf) ? "added" : leaf != t.Leaf || t.Files.Any(changed.Contains) ? "changed" : null;

        var generated = headFacts.Files.Where(f => f.Generator is not null).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var candidates = PatternDiscovery.Candidates(headFacts.Types, generated);
        var instances = baseFacts.Patterns.ToDictionary(p => p.Id, p => PatternDiscovery.InstancesOf(p.Keys, candidates), StringComparer.Ordinal);
        var baseTypes = baseFacts.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var atBase = new PatternFeatures(baseFacts, baseFacts.Patterns,
            baseFacts.Patterns.ToDictionary(p => p.Id, p => p.Instances.Select(n => baseTypes[n]).ToList(), StringComparer.Ordinal));
        var atHead = new PatternFeatures(headFacts, baseFacts.Patterns, instances);
        var reported = new Dictionary<string, List<PatternInstance>>(StringComparer.Ordinal);
        foreach (var type in candidates)
        {
            if (StatusOf(type) is not { } status) continue;
            var of = baseFacts.Patterns.Where(p => instances[p.Id].Contains(type)).ToList();
            foreach (var p in PatternDiscovery.ReportedUnder(of))
            {
                if (!reported.TryGetValue(p.Id, out var list)) reported[p.Id] = list = [];
                var head = atHead.Of(type, p);
                var lacks = p.Norms.Select(n => n.Feature).Where(f => !head.Contains(f)).ToList();
                // A changed instance departs only from the norms it had before the change, since a norm it lacked then is
                // the state of the system and not the change's doing.
                var before = status == "changed" && baseTypes.TryGetValue(type.Name, out var b) ? atBase.Of(b, p) : null;
                var others = instances[p.Id].Where(t => t.Name != type.Name).SelectMany(t => PatternDiscovery.KeysOf(t.Traits)).ToHashSet(StringComparer.Ordinal);
                var companions = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var other in baseFacts.Patterns.Where(o => o.Id != p.Id))
                    if (atHead.CompanionOf(type, p, other.Id) is { } companion) companions[other.Id] = companion;
                list.Add(new PatternInstance(type.Name, status)
                {
                    Departures = before is null ? lacks : lacks.Where(before.Contains).ToList(),
                    Lacks = lacks,
                    Unusual = PatternDiscovery.KeysOf(type.Traits).Where(k => !p.Keys.Contains(k) && !others.Contains(k)).ToList(),
                    Companions = companions,
                });
            }
        }
        string NameOf(string id) => baseFacts.Patterns.FirstOrDefault(p => p.Id == id)?.Name ?? id;

        // The examples of every reported instance, and then the texts of every file that declares a type the patterns show,
        // read at once.
        var added = candidates.Where(t => StatusOf(t) == "added").Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var typeOf = candidates.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var shown = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in baseFacts.Patterns.Where(p => reported.ContainsKey(p.Id)))
        {
            var features = instances[p.Id].ToDictionary(t => t.Name, t => atHead.Of(t, p), StringComparer.Ordinal);
            var arguments = instances[p.Id].ToDictionary(t => t.Name, t => atHead.KeyArguments(t, p).ToList(), StringComparer.Ordinal);
            var chosen = PatternExamples.Choose(reported[p.Id].Select(i => typeOf[i.Type]), instances[p.Id], added, features, arguments);
            reported[p.Id] = reported[p.Id].Select(i => i with { Examples = new ExampleChoice(chosen[i.Type].Nearest, chosen[i.Type].Typical) }).ToList();
            shown[p.Id] = reported[p.Id].SelectMany(i => new[] { i.Type, i.Examples.Nearest, i.Examples.Typical }).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        }
        var paths = shown.Values.SelectMany(n => n).SelectMany(n => typeOf[n].Files).ToHashSet(StringComparer.Ordinal);
        var texts = read is null || paths.Count == 0 ? new Dictionary<string, string>() : read(paths);

        return baseFacts.Patterns.Where(p => reported.ContainsKey(p.Id))
            .Select(p =>
            {
                var norms = p.Norms.Select(n => new NormChange(n.Feature, n.Share, PatternFeatures.Text(n.Feature, NameOf), PatternFeatures.Absence(n.Feature, NameOf))).ToList();
                var outlines = shown[p.Id].Select(n => (Name: n, Outline: PatternExamples.OutlineOf(n, headFacts.Declarations, texts)))
                    .Where(x => x.Outline is not null).ToDictionary(x => x.Name, x => x.Outline!, StringComparer.Ordinal);
                var mine = reported[p.Id].Select(i => i.Type).ToHashSet(StringComparer.Ordinal);
                var others = instances[p.Id].Where(t => !mine.Contains(t.Name)).Select(t => new PatternOther(t.Name, t.Leaf)).ToList();
                var withRows = reported[p.Id].Select(i =>
                {
                    if (!outlines.TryGetValue(i.Type, out var left)) return i;
                    var departures = i.Departures.Select(f => (f, norms.First(n => n.Feature == f).Absence)).ToList();
                    var unusual = i.Unusual.Select(k => "only this instance " + PatternFeatures.Text(k, NameOf));
                    var compare = new SortedDictionary<string, List<CompareRow>>(StringComparer.Ordinal);
                    foreach (var (name, right) in outlines.Where(o => o.Key != i.Type))
                        compare[name] = PatternExamples.Mark(PatternExamples.Align(left, right), left, departures, unusual);
                    return i with { Compare = compare };
                }).ToList();
                var samples = PatternExamples.Typical(p.Instances, p.Instances.ToDictionary(n => n, n => atBase.Of(baseTypes[n], p), StringComparer.Ordinal));
                return new PatternChange(p.Id, p.Name, p.Purpose, p.By, p.Keys, p.Instances.Count, withRows, others)
                {
                    Norms = norms,
                    Samples = samples.Take(Samples).ToList(),
                    Sources = new SortedDictionary<string, TypeSource>(outlines.ToDictionary(o => o.Key, o => o.Value.Source), StringComparer.Ordinal),
                };
            })
            .OrderByDescending(p => p.Instances.Count).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }
}
