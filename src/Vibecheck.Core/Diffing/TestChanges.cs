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

/// <summary>
/// A test method that a change added, removed or changed, with that status: <c>added</c>, <c>removed</c> or
/// <c>changed</c> when a hunk changes one of its lines. The declarations are those it references, at either end and in
/// either way of <see cref="TestReferences"/>, that the declaration diff lists as changed.
/// </summary>
public sealed record TestMethodChange(string Symbol, string Status, List<TestedDeclaration> Declarations);

/// <summary>A changed declaration that a test method references: its symbol, as <see cref="DeclarationChange.Symbol"/> gives it, and how it changed.</summary>
public sealed record TestedDeclaration(string Symbol, string Change);

/// <summary>
/// A test method that references a decision's declarations: its symbol, its status in the change, which is
/// <c>added</c>, <c>removed</c>, <c>changed</c> or <c>unchanged</c>, and the way it references them, with the helper
/// that it calls when it is through one. See <c>docs/tests.md</c> section 5.1.
/// </summary>
public sealed record DecisionTest(string Method, string Status, string Way, string? Helper = null);

/// <summary>Lists the test types a changeset changes, and the test methods that reference each decision.</summary>
public static class TestChanges
{
    private static readonly string[] StatusOrder = ["removed", "added", "changed", "unchanged"];

    /// <summary>
    /// Returns the test types with a changed file at either end, ordered by name, and the decisions with the test
    /// methods that reference them. A test method references a decision when it references one of the decision's
    /// declarations at head, or at base when the declaration was there, in either way of <see cref="TestReferences"/>.
    /// A test method at both ends is changed when a hunk adds a line within its lines at head or removes one within its
    /// lines at base.
    /// </summary>
    public static (List<TestTypeChange> Types, List<Decision> Decisions) Build(Facts baseFacts, Facts headFacts, IReadOnlyList<FileChange> files,
        IReadOnlyList<FileHunks> hunks, IReadOnlyList<DeclarationChange> declarations, IReadOnlyList<Decision> decisions)
    {
        var statuses = MethodStatuses(baseFacts, headFacts, files, hunks);
        var baseReferences = TestReferences.Of(baseFacts.Tests);
        var headReferences = TestReferences.Of(headFacts.Tests);
        TestReference? ReferenceOf(string method, string? baseSymbol, string? headSymbol)
        {
            TestReference? atHead = headSymbol is not null && headReferences.TryGetValue(method, out var h) ? h.GetValueOrDefault(headSymbol) : null;
            TestReference? atBase = baseSymbol is not null && baseReferences.TryGetValue(method, out var b) ? b.GetValueOrDefault(baseSymbol) : null;
            if (atHead?.Way == TestReference.Direct) return atHead;
            return atBase?.Way == TestReference.Direct ? atBase : atHead ?? atBase;
        }

        var tested = decisions.Select(d =>
        {
            var tests = new Dictionary<string, TestReference>(StringComparer.Ordinal);
            foreach (var x in d.Declarations)
            {
                var baseSymbol = x.Change == "added" ? null : x.Previous ?? x.Symbol;
                var headSymbol = x.Change == "removed" ? null : x.Symbol;
                foreach (var method in statuses.Keys)
                {
                    if (ReferenceOf(method, baseSymbol, headSymbol) is not { } reference) continue;
                    if (!tests.TryGetValue(method, out var known) || (known.Way != TestReference.Direct && reference.Way == TestReference.Direct)) tests[method] = reference;
                }
            }
            return d with
            {
                Tests = tests.Select(kv => new DecisionTest(kv.Key, statuses[kv.Key], kv.Value.Way, kv.Value.Helper))
                    .OrderBy(t => Array.IndexOf(StatusOrder, t.Status)).ThenBy(t => t.Method, StringComparer.Ordinal).ToList(),
            };
        }).ToList();

        var changed = files.Where(f => f.Test && f.Status != "unchanged").ToList();
        if (changed.Count == 0) return ([], tested);
        // Each changed test file's path in the changeset, by its path at each end.
        var atHead = changed.Where(f => f.Status != "removed").ToDictionary(f => f.Path, f => f.Path, StringComparer.Ordinal);
        var atBase = changed.Where(f => f.Status != "added").ToDictionary(f => f.PreviousPath ?? f.Path, f => f.Path, StringComparer.Ordinal);
        List<TestedDeclaration> DeclarationsOf(string method) => declarations
            .Where(c => ReferenceOf(method, c.BaseSymbol, c.HeadSymbol) is not null)
            .Select(c => new TestedDeclaration(c.Symbol, c.Change)).ToList();

        var baseTypes = baseFacts.Tests.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var headTypes = headFacts.Tests.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var types = new List<TestTypeChange>();
        foreach (var name in baseTypes.Keys.Union(headTypes.Keys).Order(StringComparer.Ordinal))
        {
            baseTypes.TryGetValue(name, out var b);
            headTypes.TryGetValue(name, out var h);
            var touched = (h?.Files.Where(atHead.ContainsKey).Select(p => atHead[p]) ?? [])
                .Concat(b?.Files.Where(atBase.ContainsKey).Select(p => atBase[p]) ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (touched.Count == 0) continue;
            var methods = (b?.Methods ?? []).Concat(h?.Methods ?? []).Select(m => m.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Where(m => statuses[m] != "unchanged").Select(m => new TestMethodChange(m, statuses[m], DeclarationsOf(m))).ToList();
            var status = b is null ? "added" : h is null ? "removed" : "modified";
            types.Add(new TestTypeChange(name, status, (h ?? b)!.Subject, touched, methods));
        }
        return (types, tested);
    }

    /// <summary>Returns the status of every test method at either end: <c>added</c>, <c>removed</c>, <c>changed</c> or <c>unchanged</c>.</summary>
    private static Dictionary<string, string> MethodStatuses(Facts baseFacts, Facts headFacts, IReadOnlyList<FileChange> files, IReadOnlyList<FileHunks> hunks)
    {
        var (baseLines, headLines) = ChangedLines(files, hunks);
        var baseMethods = baseFacts.Tests.Types.SelectMany(t => t.Methods).ToLookup(m => m.Symbol, StringComparer.Ordinal);
        var headMethods = headFacts.Tests.Types.SelectMany(t => t.Methods).ToLookup(m => m.Symbol, StringComparer.Ordinal);
        bool Touches(IEnumerable<TestMethodFact> methods, Dictionary<string, HashSet<int>> lines) =>
            methods.Any(m => lines.TryGetValue(m.File, out var set) && set.Any(l => m.First <= l && l <= m.Last));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in baseMethods.Select(g => g.Key).Union(headMethods.Select(g => g.Key), StringComparer.Ordinal))
        {
            result[symbol] = !baseMethods.Contains(symbol) ? "added" : !headMethods.Contains(symbol) ? "removed"
                : Touches(headMethods[symbol], headLines) || Touches(baseMethods[symbol], baseLines) ? "changed" : "unchanged";
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
