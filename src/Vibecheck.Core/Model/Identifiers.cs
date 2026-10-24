using System.Text;
using System.Text.RegularExpressions;

namespace Vibecheck.Model;

/// <summary>
/// Provides the rules for node identifiers. An identifier is a sequence of segments
/// matching <c>[a-z0-9-]+</c> joined by dots, and a node's parent is the identifier
/// with its last segment removed. The pseudo-identifier <see cref="Root"/> names the
/// level above the top-level nodes.
/// </summary>
public static partial class Identifiers
{
    /// <summary>The identifier of the level that contains the top-level nodes.</summary>
    public const string Root = "root";

    [GeneratedRegex("^[a-z0-9-]+(\\.[a-z0-9-]+)*$")]
    private static partial Regex ValidPattern();

    /// <summary>Reports whether a string is a well-formed node identifier.</summary>
    public static bool IsValid(string id) => ValidPattern().IsMatch(id);

    /// <summary>Returns the parent identifier, or <see langword="null"/> for a top-level node.</summary>
    public static string? ParentOf(string id)
    {
        var i = id.LastIndexOf('.');
        return i < 0 ? null : id[..i];
    }

    /// <summary>Returns the parent identifier, or <see cref="Root"/> for a top-level node.</summary>
    public static string ParentOrRoot(string id) => ParentOf(id) ?? Root;

    /// <summary>Returns the top-level node that contains an identifier.</summary>
    public static string TopLevelOf(string id)
    {
        var i = id.IndexOf('.');
        return i < 0 ? id : id[..i];
    }

    /// <summary>
    /// Reports whether an identifier is equal to an ancestor or lies beneath it. Every
    /// identifier lies beneath <see cref="Root"/>.
    /// </summary>
    public static bool IsWithin(string id, string ancestor) =>
        ancestor == Root || id == ancestor || id.StartsWith(ancestor + ".", StringComparison.Ordinal);

    /// <summary>Returns the number of segments in an identifier, which is zero for <see cref="Root"/>.</summary>
    public static int Depth(string id) => id == Root ? 0 : id.Count(c => c == '.') + 1;

    /// <summary>
    /// Lifts a pair of distinct leaf identifiers to their lowest common ancestor. The
    /// result names the level, which is <see cref="Root"/> or a node identifier, and the
    /// child of that level on each side.
    /// </summary>
    public static (string Level, string From, string To) Lift(string a, string b)
    {
        var sa = a.Split('.');
        var sb = b.Split('.');
        var n = 0;
        while (n < sa.Length && n < sb.Length && sa[n] == sb[n]) n++;
        var level = n == 0 ? Root : string.Join('.', sa.Take(n));
        return (level, string.Join('.', sa.Take(n + 1)), string.Join('.', sb.Take(n + 1)));
    }

    /// <summary>
    /// Normalizes a project or directory name into one identifier segment. Letters are
    /// lowercased and every other character, including dots, becomes a hyphen. Runs of
    /// hyphens collapse and leading or trailing hyphens are removed.
    /// </summary>
    public static string NormalizeSegment(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var pendingHyphen = false;
        foreach (var ch in raw)
        {
            var c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && sb.Length > 0) sb.Append('-');
                pendingHyphen = false;
                sb.Append(c);
            }
            else
            {
                pendingHyphen = true;
            }
        }
        return sb.Length == 0 ? "x" : sb.ToString();
    }
}
