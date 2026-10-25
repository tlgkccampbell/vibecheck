namespace Vibecheck.Analysis;

/// <summary>
/// Names the test frameworks the tool recognizes, the attributes that make a method a test method or skip it, and the
/// assertion libraries. The skeleton, the analysis and the checker all read these lists. See <c>docs/tests.md</c>
/// section 3.2.
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

    /// <summary>
    /// The assembly name patterns of the assertion libraries whose every method is an assertion: FluentAssertions,
    /// Shouldly and TUnit's assertions. In the test frameworks' own assemblies only the assertion types count.
    /// </summary>
    public static readonly IReadOnlyList<string> AssertionLibraries = ["FluentAssertions*", "Shouldly", "TUnit.Assertions*"];

    /// <summary>
    /// Returns a value indicating whether a method is an assertion: one declared by an assertion library, or by a type of
    /// a test framework whose name ends in <c>Assert</c>, such as <c>Assert</c>, <c>ClassicAssert</c> or <c>StringAssert</c>.
    /// </summary>
    /// <param name="assembly">The name of the assembly that declares the method.</param>
    /// <param name="type">The name of the type that declares the method, without its namespace.</param>
    public static bool IsAssertion(string assembly, string type) =>
        Matches(AssertionLibraries, assembly) || (IsFramework(assembly) && type.EndsWith("Assert", StringComparison.Ordinal));

    /// <summary>
    /// Returns a value indicating whether a test framework's attribute skips the method that carries it: <c>Skip</c> set
    /// on an xUnit attribute, <c>Ignore</c> in NUnit or MSTest, or <c>Skip</c> in TUnit.
    /// </summary>
    /// <param name="assembly">The name of the assembly that declares the attribute.</param>
    /// <param name="attribute">The name of the attribute's type, without its namespace.</param>
    /// <param name="named">The attribute's named arguments that are set to a text that is not empty.</param>
    public static bool Skips(string assembly, string attribute, IEnumerable<string> named) =>
        IsFramework(assembly) && (attribute is "IgnoreAttribute" or "SkipAttribute" || (TestAttributes.Contains(attribute) && named.Contains("Skip")));

    private static bool Matches(IEnumerable<string> patterns, string assembly) =>
        patterns.Any(p => p.EndsWith('*') ? assembly.StartsWith(p[..^1], StringComparison.Ordinal) : assembly == p);

    /// <summary>Returns a value indicating whether an assembly is one of a test framework's.</summary>
    public static bool IsFramework(string assembly) => Matches(Assemblies, assembly);

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
