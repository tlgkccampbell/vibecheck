using Vibecheck.Model;

namespace Vibecheck.Analysis;

/// <summary>
/// Decides which top-level types are outside the system the map describes. The
/// map's <c>exclude</c> list holds type patterns, and a type that any of them
/// matches is not part of the system: it is not partitioned, no leaf holds it, a
/// reference to it or from it creates no edge, and a file that declares only
/// excluded types is not a file of the map. See <c>docs/vibecheck-spec.md</c>
/// section 2.7. The map's <c>tests</c> list names the test types, which are outside
/// the system in the same way but which the checker keeps as evidence, as
/// <c>docs/tests.md</c> section 3.1 describes. A type that both lists match is excluded.
/// </summary>
public sealed class Exclusion
{
    private readonly List<TypePattern> patterns;
    private readonly List<TypePattern> tests;

    private Exclusion(List<TypePattern> patterns, List<TypePattern> tests)
    {
        this.patterns = patterns;
        this.tests = tests;
    }

    /// <summary>An exclusion that excludes nothing.</summary>
    public static readonly Exclusion None = new([], []);

    /// <summary>Parses a map's exclude and tests patterns, adding a message for each malformed one and keeping the rest.</summary>
    public static Exclusion Parse(ArchitectureMap map, List<string> errors) => Parse(map.Exclude, map.Tests, errors);

    /// <summary>Parses exclude and tests patterns, adding a message for each malformed one and keeping the rest.</summary>
    public static Exclusion Parse(IEnumerable<string> exclude, IEnumerable<string> tests, List<string> errors) =>
        new(ParseList(exclude, "exclude", errors), ParseList(tests, "tests", errors));

    private static List<TypePattern> ParseList(IEnumerable<string> texts, string list, List<string> errors)
    {
        var patterns = new List<TypePattern>();
        foreach (var text in texts)
        {
            if (TypePattern.TryParse(text, out var pattern, out var error)) patterns.Add(pattern!);
            else errors.Add($"{list}: {error}");
        }
        return patterns;
    }

    /// <summary>Reports whether the map excludes a type.</summary>
    public bool Excludes(TypeInfo type) => patterns.Any(p => p.Matches(type.PlainName, type.Assembly));

    /// <summary>Reports whether a type is a test type, which a tests pattern matches and no exclude pattern does.</summary>
    public bool IsTest(TypeInfo type) => !Excludes(type) && tests.Any(p => p.Matches(type.PlainName, type.Assembly));

    /// <summary>
    /// Returns the system part of an analysis: the analysis with every excluded type and every test type removed, along
    /// with the dependencies, external references, unresolvable references and sites that start or end at one, their
    /// declarations, and the files that declare nothing else. The counts say how many excluded types, and how many
    /// files declaring only excluded types, were removed. Test types are not counted.
    /// </summary>
    public (AnalysisResult Analysis, ExcludedCounts Counts) Apply(AnalysisResult analysis)
    {
        if (patterns.Count == 0 && tests.Count == 0) return (analysis, new ExcludedCounts(0, 0));
        var excluded = analysis.Types.Where(Excludes).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var removed = analysis.Types.Where(t => excluded.Contains(t.Name) || IsTest(t)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (removed.Count == 0) return (analysis, new ExcludedCounts(0, 0));

        var keptFiles = new HashSet<string>(StringComparer.Ordinal);
        var testFiles = new HashSet<string>(StringComparer.Ordinal);
        var removedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in analysis.Types)
        {
            var into = !removed.Contains(type.Name) ? keptFiles : excluded.Contains(type.Name) ? removedFiles : testFiles;
            foreach (var file in type.Files) into.Add(file);
        }
        removedFiles.UnionWith(testFiles);
        removedFiles.ExceptWith(keptFiles);
        var excludedFiles = removedFiles.Count(f => !testFiles.Contains(f));

        var result = analysis with
        {
            Files = analysis.Files.Where(f => !removedFiles.Contains(f.Path)).ToList(),
            Types = analysis.Types.Where(t => !removed.Contains(t.Name)).ToList(),
            Dependencies = analysis.Dependencies.Where(d => !removed.Contains(d.FromType) && !removed.Contains(d.ToType)).ToList(),
            ExternalReferences = analysis.ExternalReferences.Where(e => !removed.Contains(e.FromType)).ToList(),
            Unresolvable = analysis.Unresolvable.Where(u => !removed.Contains(u.FromType)).ToList(),
            Declarations = analysis.Declarations.Where(d => !removed.Contains(d.Type)).ToList(),
            Sites = analysis.Sites.Where(s => !removed.Contains(s.FromType) && !removed.Contains(s.ToType)).ToList(),
        };
        return (result, new ExcludedCounts(excluded.Count, excludedFiles));
    }
}

/// <summary>How many top-level types, and how many files declaring nothing but those types, a map's exclude list removed from an analysis.</summary>
public sealed record ExcludedCounts(int Types, int Files);
