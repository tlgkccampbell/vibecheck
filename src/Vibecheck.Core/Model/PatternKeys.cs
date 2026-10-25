namespace Vibecheck.Model;

/// <summary>
/// Provides the rules for pattern keys. A key is a trait that types can share, written as its kind, a colon and its
/// value, such as <c>implements:MassTransit.IConsumer&lt;&gt;</c>. The value of an <see cref="Inherits"/>,
/// <see cref="Implements"/> or <see cref="Attribute"/> key is the open name of a type, and the value of a
/// <see cref="Suffix"/> key is the text a type's name ends with. See <c>docs/patterns.md</c> section 4.1.
/// </summary>
public static class PatternKeys
{
    /// <summary>The kind of a key that a type carries when it inherits the class the key names.</summary>
    public const string Inherits = "inherits";

    /// <summary>The kind of a key that a type carries when it implements the interface the key names directly.</summary>
    public const string Implements = "implements";

    /// <summary>The kind of a key that a type carries when the attribute the key names is applied to it.</summary>
    public const string Attribute = "attribute";

    /// <summary>The kind of a key that a type carries when its name ends with the key's text. Discovery never uses it.</summary>
    public const string Suffix = "suffix";

    /// <summary>The kinds of key, in the order in which a pattern lists its keys.</summary>
    public static readonly IReadOnlyList<string> Kinds = [Inherits, Implements, Attribute, Suffix];

    /// <summary>Returns the key of a kind and a value.</summary>
    public static string Of(string kind, string value) => kind + ":" + value;

    /// <summary>Returns the kind of a well-formed key.</summary>
    public static string KindOf(string key) => key[..key.IndexOf(':')];

    /// <summary>Returns the value of a well-formed key.</summary>
    public static string ValueOf(string key) => key[(key.IndexOf(':') + 1)..];

    /// <summary>Checks whether a key is well formed.</summary>
    /// <returns>A sentence fragment that says what is wrong with the key, or <see langword="null"/> when it is well formed.</returns>
    public static string? Problem(string key)
    {
        var colon = key.IndexOf(':');
        if (colon < 0 || !Kinds.Contains(key[..colon])) return $"\"{key}\" is not a key; write one of {string.Join(", ", Kinds.Select(k => k + ":…"))}";
        if (string.IsNullOrWhiteSpace(key[(colon + 1)..])) return $"\"{key}\" names no type";
        return null;
    }

    /// <summary>Returns the position of a key's kind in <see cref="Kinds"/>, by which a pattern orders its keys.</summary>
    public static int Rank(string key) => Kinds.ToList().IndexOf(KindOf(key));

    /// <summary>
    /// Returns the name a pattern takes from a key when nothing else names it: the kind, then the value's last segment,
    /// such as <c>implements IConsumer&lt;&gt;</c>.
    /// </summary>
    public static string NameOf(string key)
    {
        var value = ValueOf(key);
        return KindOf(key) + " " + (KindOf(key) == Suffix ? value : value[(value.LastIndexOf('.') + 1)..]);
    }
}
