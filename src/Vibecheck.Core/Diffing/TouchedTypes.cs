using Vibecheck.Checking;

namespace Vibecheck.Diffing;

/// <summary>
/// Answers how a change touched a type at head, which is how the patterns and the glossary mark the types they report.
/// </summary>
public static class TouchedTypes
{
    /// <summary>The status of a type that is new at head.</summary>
    public const string Added = "added";

    /// <summary>The status of a type that binds to another leaf at head, or one of whose files changed.</summary>
    public const string Changed = "changed";

    /// <summary>
    /// Returns a function that gets how the change touched a type at head: <see cref="Added"/>, <see cref="Changed"/>,
    /// or <see langword="null"/> when the change left it alone.
    /// </summary>
    /// <param name="baseFacts">The facts at base, whose types tell which types are new and where each was bound.</param>
    /// <param name="files">Every file of the changeset, with its status.</param>
    public static Func<TypeFact, string?> StatusOf(Facts baseFacts, IEnumerable<FileChange> files)
    {
        var leafAtBase = baseFacts.Types.ToDictionary(t => t.Name, t => t.Leaf, StringComparer.Ordinal);
        var changed = files.Where(f => f.Status != "unchanged").Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        return t => !leafAtBase.TryGetValue(t.Name, out var leaf) ? Added : leaf != t.Leaf || t.Files.Any(changed.Contains) ? Changed : null;
    }
}
