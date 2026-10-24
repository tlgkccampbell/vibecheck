namespace Vibecheck.Model;

/// <summary>
/// Defines the broad categories a node can be given, so that a viewer can tell nodes apart by their role at a glance.
/// The list is fixed, so that every map uses the same names and a viewer can give each one a stable color and icon.
/// Section 2.1 of the specification says what each category covers.
/// </summary>
public static class Categories
{
    /// <summary>Gets the category names, in the order the specification lists them.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["entry", "feature", "domain", "analysis", "transform", "integration", "foundation", "tooling"];

    /// <summary>Checks whether a name is one of the categories.</summary>
    /// <param name="name">The name to check.</param>
    /// <returns><see langword="true"/> when the name is a category; otherwise, <see langword="false"/>.</returns>
    public static bool IsKnown(string name) => Names.Contains(name, StringComparer.Ordinal);
}
