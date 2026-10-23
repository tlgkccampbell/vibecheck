using System.Text.RegularExpressions;

namespace Archmap.Model;

/// <summary>
/// A pattern over fully qualified top-level type names, as a leaf's bind list holds.
/// <c>*</c> matches any characters other than a dot, <c>**</c> matches any characters
/// including dots, and an optional <c>Assembly::</c> prefix restricts the pattern to
/// types declared in that assembly. Specificity is the number of literal characters,
/// counting the assembly qualifier, and decides between leaves whose patterns both
/// match a type. See <c>docs/archmap-spec.md</c> section 2.2.
/// </summary>
public sealed class TypePattern
{
    private readonly Regex regex;

    private TypePattern(string text, string? assembly, string body, Regex regex)
    {
        Text = text;
        Assembly = assembly;
        Body = body;
        this.regex = regex;
        Specificity = body.Count(c => c != '*') + (assembly?.Length ?? 0);
    }

    /// <summary>The pattern as written in the map.</summary>
    public string Text { get; }

    /// <summary>The assembly the pattern is restricted to, or <see langword="null"/> for every assembly.</summary>
    public string? Assembly { get; }

    /// <summary>The type-name part of the pattern, without the assembly qualifier.</summary>
    public string Body { get; }

    public int Specificity { get; }

    /// <summary>The whole namespace segments before the first wildcard, used to place proposed leaves. A pattern without a wildcard yields its whole body.</summary>
    public string LiteralPrefix
    {
        get
        {
            var star = Body.IndexOf('*');
            if (star < 0) return Body;
            var literal = Body[..star];
            var dot = literal.LastIndexOf('.');
            return dot < 0 ? "" : literal[..dot];
        }
    }

    /// <summary>Parses a pattern, reporting why it is malformed when it is.</summary>
    public static bool TryParse(string text, out TypePattern? pattern, out string? error)
    {
        pattern = null;
        error = null;
        string? assembly = null;
        var body = text;
        var sep = text.IndexOf("::", StringComparison.Ordinal);
        if (sep >= 0)
        {
            assembly = text[..sep];
            body = text[(sep + 2)..];
            if (assembly.Length == 0) { error = $"pattern \"{text}\" has an empty assembly qualifier"; return false; }
            if (body.Contains("::", StringComparison.Ordinal)) { error = $"pattern \"{text}\" has more than one assembly qualifier"; return false; }
        }
        if (body.Length == 0) { error = $"pattern \"{text}\" has no type pattern"; return false; }
        if (body.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '.' or '*'))) { error = $"pattern \"{text}\" contains a character that cannot appear in a type name"; return false; }
        if (body.Contains("***", StringComparison.Ordinal)) { error = $"pattern \"{text}\" has more than two consecutive wildcards"; return false; }

        var escaped = Regex.Escape(body).Replace("\\*\\*", "\u0001").Replace("\\*", "[^.]*").Replace("\u0001", ".*");
        pattern = new TypePattern(text, assembly, body, new Regex("^" + escaped + "$", RegexOptions.CultureInvariant));
        return true;
    }

    /// <summary>Parses a pattern, throwing when it is malformed.</summary>
    public static TypePattern Parse(string text) =>
        TryParse(text, out var p, out var error) ? p! : throw new ArgumentException(error);

    /// <summary>Reports whether the pattern matches a type of the given fully qualified name declared in the given assembly.</summary>
    public bool Matches(string typeName, string assembly) =>
        (Assembly is null || Assembly == assembly) && regex.IsMatch(typeName);
}
