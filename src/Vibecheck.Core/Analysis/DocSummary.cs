using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vibecheck.Analysis;

/// <summary>Extracts the first sentence of a type's <c>&lt;summary&gt;</c> from its documentation comment XML.</summary>
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
        var text = Whitespace().Replace(string.Concat(summary.DescendantNodes().OfType<XText>().Select(t => t.Value)), " ").Trim();
        if (text.Length == 0) return null;
        var end = FirstSentenceEnd(text);
        return end < 0 ? text : text[..(end + 1)];
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
