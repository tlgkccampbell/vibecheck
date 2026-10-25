using Vibecheck.Checking;

namespace Vibecheck.Diffing;

/// <summary>
/// A test type that a changeset changes: one with a changed file at either end. The status is <c>added</c>,
/// <c>removed</c> or <c>modified</c>. The subject is the type's at head, or at base when it was removed, and is
/// <see langword="null"/> for test infrastructure. The files are the type's changed files, by their paths in the
/// changeset, and the methods are its test methods that the change added, removed or changed. See
/// <c>docs/tests.md</c> section 7.3.
/// </summary>
public sealed record TestTypeChange(string Name, string Status, SubjectFact? Subject, List<string> Files, List<TestMethodChange> Methods);

/// <summary>A test method that a change added, removed or changed, with that status: <c>added</c>, <c>removed</c> or <c>changed</c> when a hunk changes one of its lines.</summary>
public sealed record TestMethodChange(string Symbol, string Status);

/// <summary>Lists the test types a changeset changes, with the test methods it changes in each.</summary>
public static class TestChanges
{
    /// <summary>
    /// Returns the test types with a changed file at either end, ordered by name. A test method at both ends is changed
    /// when a hunk adds a line within its lines at head or removes one within its lines at base.
    /// </summary>
    public static List<TestTypeChange> Build(Facts baseFacts, Facts headFacts, IReadOnlyList<FileChange> files, IReadOnlyList<FileHunks> hunks)
    {
        var changed = files.Where(f => f.Test && f.Status != "unchanged").ToList();
        if (changed.Count == 0) return [];
        // Each changed test file's path in the changeset, by its path at each end.
        var atHead = changed.Where(f => f.Status != "removed").ToDictionary(f => f.Path, f => f.Path, StringComparer.Ordinal);
        var atBase = changed.Where(f => f.Status != "added").ToDictionary(f => f.PreviousPath ?? f.Path, f => f.Path, StringComparer.Ordinal);
        var (baseLines, headLines) = ChangedLines(files, hunks);

        var baseTypes = baseFacts.Tests.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var headTypes = headFacts.Tests.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var result = new List<TestTypeChange>();
        foreach (var name in baseTypes.Keys.Union(headTypes.Keys).Order(StringComparer.Ordinal))
        {
            baseTypes.TryGetValue(name, out var b);
            headTypes.TryGetValue(name, out var h);
            var touched = (h?.Files.Where(atHead.ContainsKey).Select(p => atHead[p]) ?? [])
                .Concat(b?.Files.Where(atBase.ContainsKey).Select(p => atBase[p]) ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (touched.Count == 0) continue;

            var baseMethods = (b?.Methods ?? []).ToLookup(m => m.Symbol, StringComparer.Ordinal);
            var headMethods = (h?.Methods ?? []).ToLookup(m => m.Symbol, StringComparer.Ordinal);
            bool Touches(IEnumerable<TestMethodFact> methods, Dictionary<string, HashSet<int>> lines) =>
                methods.Any(m => lines.TryGetValue(m.File, out var set) && set.Any(l => m.First <= l && l <= m.Last));
            var methods = new List<TestMethodChange>();
            foreach (var symbol in baseMethods.Select(g => g.Key).Union(headMethods.Select(g => g.Key)).Order(StringComparer.Ordinal))
            {
                var status = !baseMethods.Contains(symbol) ? "added" : !headMethods.Contains(symbol) ? "removed"
                    : Touches(headMethods[symbol], headLines) || Touches(baseMethods[symbol], baseLines) ? "changed" : null;
                if (status is not null) methods.Add(new TestMethodChange(symbol, status));
            }
            var typeStatus = b is null ? "added" : h is null ? "removed" : "modified";
            result.Add(new TestTypeChange(name, typeStatus, (h ?? b)!.Subject, touched, methods));
        }
        return result;
    }

    /// <summary>Returns the one-based numbers of the lines that the hunks remove, by the file's path at base, and of those they add, by its path at head.</summary>
    private static (Dictionary<string, HashSet<int>> Base, Dictionary<string, HashSet<int>> Head) ChangedLines(IReadOnlyList<FileChange> files, IReadOnlyList<FileHunks> hunks)
    {
        var previous = files.Where(f => f.PreviousPath is not null).ToDictionary(f => f.Path, f => f.PreviousPath!, StringComparer.Ordinal);
        var removed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var added = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var file in hunks)
        {
            var atBase = removed[previous.GetValueOrDefault(file.Path, file.Path)] = [];
            var atHead = added[file.Path] = [];
            foreach (var hunk in file.Hunks)
            {
                if (!hunk.TryRanges(out var old, out var now)) continue;
                int b = old.Start + 1, h = now.Start + 1;
                foreach (var line in hunk.Lines)
                {
                    switch (line[0])
                    {
                        case "-": atBase.Add(b++); break;
                        case "+": atHead.Add(h++); break;
                        default: b++; h++; break;
                    }
                }
            }
        }
        return (removed, added);
    }
}
