using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vibecheck.Analysis;

/// <summary>
/// Extracts the first sentence of a declaration's <c>&lt;summary&gt;</c> from its documentation comment XML. A
/// reference inside the summary reads as what it names: a <c>langword</c> as the word, a <c>cref</c> as the simple
/// name of the type or member, and a <c>paramref</c> or <c>typeparamref</c> as the parameter's name.
/// </summary>
public static partial class DocSummary
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Returns the first sentence of the summary, or <see langword="null"/> when the XML has none.</summary>
    public static string? FirstSentence(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        var summary = root.Name == "summary" ? root : root.Descendants("summary").FirstOrDefault();
        if (summary is null) return null;
        var sb = new StringBuilder();
        Append(summary, sb);
        var text = Whitespace().Replace(sb.ToString(), " ").Trim();
        if (text.Length == 0) return null;
        var end = FirstSentenceEnd(text);
        return end < 0 ? text : text[..(end + 1)];
    }

    /// <summary>Appends the text of an element, reading each reference in it as the name it refers to.</summary>
    private static void Append(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text) sb.Append(text.Value);
            else if (node is not XElement child) continue;
            else if (child.Name == "see" || child.Name == "seealso")
            {
                if (child.Attribute("langword") is { } word) sb.Append(word.Value);
                else if (child.Attribute("cref") is { } cref) sb.Append(child.IsEmpty || string.IsNullOrWhiteSpace(child.Value) ? CrefName(cref.Value) : child.Value);
                else if (child.Attribute("href") is { } href) sb.Append(string.IsNullOrWhiteSpace(child.Value) ? href.Value : child.Value);
                else Append(child, sb);
            }
            else if (child.Name == "paramref" || child.Name == "typeparamref") sb.Append(child.Attribute("name")?.Value ?? child.Value);
            else Append(child, sb);
        }
    }

    /// <summary>
    /// Returns the name a documentation identifier such as <c>P:A.B.Term.File</c> or <c>T:A.B.Term</c> reads as: a
    /// type's own name, or a member's containing type and name, without any parameter list.
    /// </summary>
    private static string CrefName(string cref)
    {
        var colon = cref.IndexOf(':');
        var kind = colon > 0 ? cref[0] : 'T';
        var name = colon >= 0 ? cref[(colon + 1)..] : cref;
        var paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren];
        var segments = name.Split('.');
        return kind == 'T' || kind == 'N' || segments.Length < 2 ? segments[^1] : segments[^2] + "." + segments[^1];
    }

    /// <summary>Returns the index of the period that ends the first sentence, skipping periods inside identifiers such as <c>nt65.json</c>.</summary>
    private static int FirstSentenceEnd(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '.') continue;
            if (i + 1 == text.Length) return i;
            if (text[i + 1] == ' ') return i;
        }
        return -1;
    }
}
