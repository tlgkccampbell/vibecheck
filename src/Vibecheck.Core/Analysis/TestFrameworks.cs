namespace Vibecheck.Analysis;

/// <summary>
/// Names the test frameworks the tool recognizes and the attributes that make a method a test method in them. The
/// skeleton, the analysis and the checker all read this one list. See <c>docs/tests.md</c> section 3.2.
/// </summary>
public static class TestFrameworks
{
    /// <summary>The assembly name patterns of the test frameworks. A pattern that ends in <c>*</c> matches every name that starts with the text before it.</summary>
    public static readonly IReadOnlyList<string> Assemblies = ["xunit*", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework", "TUnit.Core"];

    /// <summary>
    /// The names of the attribute types that mark a test method when a test framework assembly declares them: <c>Fact</c>
    /// and <c>Theory</c> in xUnit, <c>Test</c>, <c>TestCase</c> and <c>TestCaseSource</c> in NUnit, <c>TestMethod</c> and
    /// <c>DataTestMethod</c> in MSTest, and <c>Test</c> in TUnit.
    /// </summary>
    public static readonly IReadOnlySet<string> TestAttributes = new HashSet<string>(
        ["FactAttribute", "TheoryAttribute", "TestAttribute", "TestCaseAttribute", "TestCaseSourceAttribute", "TestMethodAttribute", "DataTestMethodAttribute"], StringComparer.Ordinal);

    /// <summary>Returns a value indicating whether an assembly is one of a test framework's.</summary>
    public static bool IsFramework(string assembly) =>
        Assemblies.Any(p => p.EndsWith('*') ? assembly.StartsWith(p[..^1], StringComparison.Ordinal) : assembly == p);

    /// <summary>
    /// Returns the assemblies of the solution that are test projects, which are those whose types reference a test
    /// framework assembly. See <c>docs/vibecheck-spec.md</c> section 5.1.
    /// </summary>
    public static SortedSet<string> TestAssemblies(AnalysisResult analysis)
    {
        var assemblyOf = analysis.Types.ToDictionary(t => t.Name, t => t.Assembly, StringComparer.Ordinal);
        return new SortedSet<string>(analysis.ExternalReferences
            .Where(e => IsFramework(e.Assembly) && assemblyOf.ContainsKey(e.FromType))
            .Select(e => assemblyOf[e.FromType]), StringComparer.Ordinal);
    }
}
