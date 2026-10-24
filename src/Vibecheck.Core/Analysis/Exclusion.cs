using Vibecheck.Model;

namespace Vibecheck.Analysis;

/// <summary>
/// Decides which top-level types are outside the system the map describes. The
/// map's <c>exclude</c> list holds type patterns, and a type that any of them
/// matches is not part of the system: it is not partitioned, no leaf holds it, a
/// reference to it or from it creates no edge, and a file that declares only
/// excluded types is not a file of the map. See <c>docs/vibecheck-spec.md</c>
/// section 2.8.
/// </summary>
public sealed class Exclusion
{
    private readonly List<TypePattern> patterns;

    private Exclusion(List<TypePattern> patterns)
    {
        this.patterns = patterns;
    }

    /// <summary>An exclusion that excludes nothing.</summary>
    public static readonly Exclusion None = new([]);

    /// <summary>Parses the map's exclude patterns, adding a message for each malformed one and keeping the rest.</summary>
    public static Exclusion Parse(IEnumerable<string> texts, List<string> errors)
    {
        var patterns = new List<TypePattern>();
        foreach (var text in texts)
        {
            if (TypePattern.TryParse(text, out var pattern, out var error)) patterns.Add(pattern!);
            else errors.Add($"exclude: {error}");
        }
        return new Exclusion(patterns);
    }

    /// <summary>Reports whether the map excludes a type.</summary>
    public bool Excludes(TypeInfo type) => patterns.Any(p => p.Matches(type.PlainName, type.Assembly));

    /// <summary>
    /// Returns the analysis with every excluded type removed, along with the
    /// dependencies, external references and unresolvable references that start or
    /// end at one, and the files that declare nothing else. The counts say how many
    /// types and files were removed.
    /// </summary>
    public (AnalysisResult Analysis, ExcludedCounts Counts) Apply(AnalysisResult analysis)
    {
        if (patterns.Count == 0) return (analysis, new ExcludedCounts(0, 0));
        var excluded = analysis.Types.Where(Excludes).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (excluded.Count == 0) return (analysis, new ExcludedCounts(0, 0));

        var keptFiles = new HashSet<string>(StringComparer.Ordinal);
        var excludedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in analysis.Types)
        {
            foreach (var file in type.Files) (excluded.Contains(type.Name) ? excludedFiles : keptFiles).Add(file);
        }
        excludedFiles.ExceptWith(keptFiles);

        var result = analysis with
        {
            Files = analysis.Files.Where(f => !excludedFiles.Contains(f.Path)).ToList(),
            Types = analysis.Types.Where(t => !excluded.Contains(t.Name)).ToList(),
            Dependencies = analysis.Dependencies.Where(d => !excluded.Contains(d.FromType) && !excluded.Contains(d.ToType)).ToList(),
            ExternalReferences = analysis.ExternalReferences.Where(e => !excluded.Contains(e.FromType)).ToList(),
            Unresolvable = analysis.Unresolvable.Where(u => !excluded.Contains(u.FromType)).ToList(),
        };
        return (result, new ExcludedCounts(excluded.Count, excludedFiles.Count));
    }
}

/// <summary>How many top-level types, and how many files declaring nothing but those types, a map's exclude list removed from an analysis.</summary>
public sealed record ExcludedCounts(int Types, int Files);
