using Vibecheck.Analysis;

namespace Vibecheck.Checking;

/// <summary>
/// Builds the <c>tests</c> member of the facts: each test type with its test methods and its subject, the exercises
/// that the test types make into the system, and the calls among their members. This is the one place that decides which system type a test type tests.
/// See <c>docs/tests.md</c> sections 3.2 and 4.
/// </summary>
public static class TestTypes
{
    /// <summary>The suffixes that the rule by name removes from a test type's name, each tried before any suffix it ends with.</summary>
    internal static readonly string[] Suffixes = ["Tests", "Specs", "Should", "Test", "Spec"];

    /// <summary>The name of the rule that places a test type by its name.</summary>
    public const string ByName = "name";

    /// <summary>The name of the rule that places a test type by the system type its exercises reference most.</summary>
    public const string ByReferences = "references";

    /// <summary>Returns the test facts of an analysis, and a file fact for each file that declares test types and no system type.</summary>
    /// <param name="analysis">The whole analysis, before the map's exclusion is applied.</param>
    /// <param name="exclusion">The map's exclusion, which says which types are test types.</param>
    /// <param name="system">The analysis with the excluded and test types removed.</param>
    /// <param name="leafOf">The leaf that each type of the system binds to.</param>
    public static (TestFacts Tests, List<FileFact> Files) Build(AnalysisResult analysis, Exclusion exclusion, AnalysisResult system, IReadOnlyDictionary<string, string> leafOf)
    {
        var tests = analysis.Types.Where(exclusion.IsTest).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        if (tests.Count == 0) return (TestFacts.Empty, []);
        var names = tests.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var systemTypes = system.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var declarations = analysis.Declarations.Where(d => names.Contains(d.Type)).ToList();
        var membersByFile = declarations.Where(d => d.Kind != "type").GroupBy(d => d.File, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        // The innermost member of the test type whose lines hold the reference.
        string? MemberAt(string type, string file, int line) => membersByFile.TryGetValue(file, out var members)
            ? members.Where(d => d.Type == type && d.FirstLine <= line && line <= d.LastLine).OrderBy(d => d.LastLine - d.FirstLine).Select(d => d.Symbol).FirstOrDefault()
            : null;

        var references = analysis.Dependencies.Where(d => names.Contains(d.FromType) && systemTypes.ContainsKey(d.ToType))
            .Select(d => (From: d.FromType, To: d.ToType, d.Symbol, d.File, d.Line))
            .Concat(analysis.Sites.Where(s => names.Contains(s.FromType) && systemTypes.ContainsKey(s.ToType)).Select(s => (From: s.FromType, To: s.ToType, s.Symbol, s.File, s.Line)))
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .ToList();
        var exercises = references.Select(r => new ExerciseFact(r.From, MemberAt(r.From, r.File, r.Line), r.Symbol, r.File, r.Line)).ToList();
        var referencesOf = references.ToLookup(r => r.From, r => r.To, StringComparer.Ordinal);

        // A call joins a member of a test type to a declaration of a test type: another type's, by a dependency, or its
        // own, by a site. A reference that no member holds, such as one in an attribute on the type, is not a call.
        var calls = analysis.Dependencies.Where(d => names.Contains(d.FromType) && names.Contains(d.ToType))
            .Select(d => (d.FromType, d.Symbol, d.File, d.Line))
            .Concat(analysis.Sites.Where(s => names.Contains(s.FromType) && names.Contains(s.ToType)).Select(s => (s.FromType, s.Symbol, s.File, s.Line)))
            .Select(r => (From: MemberAt(r.FromType, r.File, r.Line), r.Symbol, r.File, r.Line))
            .Where(r => r.From is not null && r.From != r.Symbol)
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .Select(r => new CallFact(r.From!, r.Symbol, r.File, r.Line)).Distinct().ToList();

        var byShortName = system.Types.GroupBy(t => ShortName(t.PlainName), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var methodsOf = declarations.Where(d => d.Kind == "method" && d.Test is not null).ToLookup(d => d.Type, StringComparer.Ordinal);
        var types = new List<TestTypeFact>();
        foreach (var test in tests)
        {
            var methods = methodsOf[test.Name].OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.FirstLine)
                .Select(d => new TestMethodFact(d.Symbol, d.File, d.FirstLine, d.LastLine)).ToList();
            SubjectFact? subject = null;
            if (methods.Count > 0)
            {
                var type = SubjectByName(test, byShortName);
                var by = ByName;
                if (type is null) { type = SubjectByReferences(referencesOf[test.Name]); by = ByReferences; }
                if (type is not null) subject = new SubjectFact(type, leafOf[type], by);
            }
            types.Add(new TestTypeFact(test.Name, test.Assembly, test.Files, subject, methods));
        }

        // A file that declares a system type stays a file of the system; any other file of a test type is a test file.
        var systemFiles = system.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var files = new List<FileFact>();
        foreach (var file in analysis.Files.Where(f => !systemFiles.Contains(f.Path)))
        {
            var declaring = types.Where(t => t.Files.Contains(file.Path)).ToList();
            if (declaring.Count == 0) continue;
            var subject = declaring.Where(t => t.Subject is not null).GroupBy(t => t.Subject!.Leaf, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();
            files.Add(new FileFact(file.Path, file.Hash, [], file.Generator) { Test = true, Subject = subject });
        }
        return (new TestFacts(types, exercises, calls), files);
    }

    /// <summary>
    /// Returns how many test types each rule placed and how many are test infrastructure, as <c>vibecheck check</c>
    /// reports it, such as "55 test types: 40 by name, 12 by references, 3 infrastructure".
    /// </summary>
    public static string Placement(TestFacts tests)
    {
        int Placed(string by) => tests.Types.Count(t => t.Subject?.By == by);
        return $"{tests.Types.Count} test types: {Placed(ByName)} by name, {Placed(ByReferences)} by references, {tests.Types.Count(t => t.Subject is null)} infrastructure";
    }

    /// <summary>
    /// Returns the system type that a test type is named for, or <see langword="null"/>. The test type's name, with one of
    /// <see cref="Suffixes"/> removed, must be the name of a system type. When several system types have that name, the
    /// one whose namespace shares the most trailing segments with the test type's wins, and a tie gives no answer. The
    /// test type's namespace is compared without its segments that end in a suffix, so that <c>Example.Auth.Tests</c>
    /// and <c>Example.Tests.Auth</c> both compare as <c>Example.Auth</c>.
    /// </summary>
    internal static string? SubjectByName(TypeInfo test, IReadOnlyDictionary<string, List<TypeInfo>> byShortName)
    {
        var name = ShortName(test.PlainName);
        var suffix = Suffixes.FirstOrDefault(s => name.Length > s.Length && name.EndsWith(s, StringComparison.Ordinal));
        if (suffix is null || !byShortName.TryGetValue(name[..^suffix.Length], out var candidates)) return null;
        if (candidates.Count == 1) return candidates[0].Name;
        var own = test.Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries).Where(s => !Suffixes.Any(x => s.EndsWith(x, StringComparison.Ordinal))).ToArray();
        var ranked = candidates.Select(c => (c.Name, Tail: SharedTail(own, c.Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries))))
            .OrderByDescending(x => x.Tail).ToList();
        return ranked[0].Tail > ranked[1].Tail ? ranked[0].Name : null;
    }

    /// <summary>
    /// Returns the system type that a test type's exercises reference most, when it holds at least half of them and no
    /// other type is referenced as often, or <see langword="null"/>.
    /// </summary>
    /// <param name="targets">The system type that each of the test type's exercises references.</param>
    internal static string? SubjectByReferences(IEnumerable<string> targets)
    {
        var counts = targets.GroupBy(t => t, StringComparer.Ordinal).Select(g => (Type: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Type, StringComparer.Ordinal).ToList();
        if (counts.Count == 0) return null;
        var total = counts.Sum(x => x.Count);
        if (counts[0].Count * 2 < total || (counts.Count > 1 && counts[1].Count == counts[0].Count)) return null;
        return counts[0].Type;
    }

    /// <summary>Returns how many trailing segments two namespaces share.</summary>
    private static int SharedTail(string[] a, string[] b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[^(n + 1)] == b[^(n + 1)]) n++;
        return n;
    }

    /// <summary>Returns the last segment of a type's plain name.</summary>
    private static string ShortName(string plain) => plain[(plain.LastIndexOf('.') + 1)..];
}
