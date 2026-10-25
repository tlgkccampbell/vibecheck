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
    /// files changed. A type is reported under the patterns that <see cref="PatternDiscovery.ReportedUnder"/> chooses.
    /// </summary>
    /// <param name="baseFacts">The facts at base, which hold the patterns.</param>
    /// <param name="headFacts">The facts at head, whose types are the instances.</param>
    /// <param name="files">Every file of the changeset, with its status.</param>
    public static List<PatternChange> Build(Facts baseFacts, Facts headFacts, IReadOnlyList<FileChange> files)
    {
        if (baseFacts.Patterns.Count == 0) return [];
        var leafAtBase = baseFacts.Types.ToDictionary(t => t.Name, t => t.Leaf, StringComparer.Ordinal);
        var changed = files.Where(f => f.Status != "unchanged").Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        string? StatusOf(TypeFact t) =>
            !leafAtBase.TryGetValue(t.Name, out var leaf) ? "added" : leaf != t.Leaf || t.Files.Any(changed.Contains) ? "changed" : null;

        var generated = headFacts.Files.Where(f => f.Generator is not null).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var candidates = PatternDiscovery.Candidates(headFacts.Types, generated);
        var instances = baseFacts.Patterns.ToDictionary(p => p.Id, p => PatternDiscovery.InstancesOf(p.Keys, candidates), StringComparer.Ordinal);
        var reported = new Dictionary<string, List<PatternInstance>>(StringComparer.Ordinal);
        foreach (var type in candidates)
        {
            if (StatusOf(type) is not { } status) continue;
            var of = baseFacts.Patterns.Where(p => instances[p.Id].Contains(type)).ToList();
            foreach (var p in PatternDiscovery.ReportedUnder(of))
            {
                if (!reported.TryGetValue(p.Id, out var list)) reported[p.Id] = list = [];
                list.Add(new PatternInstance(type.Name, status));
            }
        }
        return baseFacts.Patterns.Where(p => reported.ContainsKey(p.Id))
            .Select(p =>
            {
                var mine = reported[p.Id].Select(i => i.Type).ToHashSet(StringComparer.Ordinal);
                var others = instances[p.Id].Where(t => !mine.Contains(t.Name)).Select(t => new PatternOther(t.Name, t.Leaf)).ToList();
                return new PatternChange(p.Id, p.Name, p.Purpose, p.By, p.Keys, p.Instances.Count, reported[p.Id], others);
            })
            .OrderByDescending(p => p.Instances.Count).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }
}
