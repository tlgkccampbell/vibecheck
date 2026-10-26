using Vibecheck.Analysis;
using Vibecheck.Checking;

namespace Vibecheck.Diffing;

/// <summary>
/// Finds where each type of a commit is declared, so that the viewer can open a type's definition from any line of a
/// diff that names it, whether or not the change touches the type.
/// </summary>
public static class TypeLocations
{
    /// <summary>
    /// Returns where each type at head is declared, system and test types alike, nested types included, ordered by name.
    /// A type declared in several files is located in the file that declares most of it, as <see cref="MainPart"/> chooses it.
    /// </summary>
    public static List<TypeLocation> Build(Facts headFacts) =>
        headFacts.Declarations.Concat(headFacts.Tests.Declarations)
            .Where(d => d.Kind == "type")
            .GroupBy(d => d.Symbol, StringComparer.Ordinal)
            .Select(g => MainPart(g)!)
            .Select(d => new TypeLocation(d.Symbol, d.File, d.FirstLine))
            .OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.File, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Returns the declaration of a type that covers the most lines, ties going to the first file by path, or
    /// <see langword="null"/> when there is none. This answers which file declares most of a partial type.
    /// </summary>
    /// <param name="parts">The declarations of one type, one for each file that declares a part of it.</param>
    public static Declaration? MainPart(IEnumerable<Declaration> parts) =>
        parts.OrderByDescending(d => d.LastLine - d.FirstLine).ThenBy(d => d.File, StringComparer.Ordinal).FirstOrDefault();
}
