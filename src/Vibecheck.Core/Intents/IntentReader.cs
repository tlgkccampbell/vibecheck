using System.Text;
using System.Text.RegularExpressions;
using Vibecheck.Model;

namespace Vibecheck.Intents;

/// <summary>Identifies what an <see cref="IntentAnchor"/> names.</summary>
public enum IntentAnchorKind
{
    /// <summary>A node or an external of the map, by its identifier.</summary>
    Node,

    /// <summary>An edge, as two ends joined by an arrow. Each end is a node, an external or a type.</summary>
    Edge,

    /// <summary>A file, by its path from the repository's top.</summary>
    File,

    /// <summary>A type, by its full name or by its simple name.</summary>
    Type,
}

/// <summary>
/// Represents one anchor of an <see cref="Intent"/>: an inline code span after the intent's claim that names a node, an
/// edge, a file or a type. See <c>docs/intent.md</c> section 3.2.
/// </summary>
/// <param name="Text">The span's text as written.</param>
/// <param name="Kind">The kind of thing the anchor names.</param>
/// <param name="Name">The node identifier, the file path or the type name, or the source end of an edge.</param>
/// <param name="To">The target end of an edge, or <see langword="null"/> for the other kinds.</param>
public sealed record IntentAnchor(string Text, IntentAnchorKind Kind, string Name, string? To = null);

/// <summary>
/// Represents an intent: the author's statement of why a change makes one of its main choices, written as an item of
/// the <c>Intent</c> section of a pull request description. An intent is a claim, never evidence, so nothing treats it
/// as true. See <c>docs/intent.md</c> section 3.1.
/// </summary>
/// <param name="Line">The 1-based line of the text on which the item starts.</param>
/// <param name="Claim">What was chosen, in one sentence, as Markdown whose only markup is inline code.</param>
/// <param name="Anchors">The anchors that follow the claim on the item's first line, in order.</param>
/// <param name="Why">The text after <c>Why:</c>, or <see langword="null"/> when the item has none.</param>
/// <param name="Alternative">The text after <c>Instead of:</c>, or <see langword="null"/> when the item has none.</param>
/// <param name="Note">The item's other text, or <see langword="null"/> when it has none.</param>
public sealed record Intent(int Line, string Claim, IReadOnlyList<IntentAnchor> Anchors, string? Why, string? Alternative, string? Note);

/// <summary>Represents an item of an <c>Intent</c> section that could not be read as an <see cref="Intent"/>.</summary>
/// <param name="Line">The 1-based line of the text on which the item starts.</param>
/// <param name="Message">What is wrong with the item.</param>
public sealed record IntentProblem(int Line, string Message);

/// <summary>Represents what <see cref="IntentReader.Parse"/> read from a text.</summary>
/// <param name="Line">The 1-based line of the section's heading, or 0 when the text has no <c>Intent</c> section.</param>
/// <param name="Intents">The intents, in the order of the section.</param>
/// <param name="Problems">The items that were skipped, and why.</param>
public sealed record ParsedIntents(int Line, IReadOnlyList<Intent> Intents, IReadOnlyList<IntentProblem> Problems)
{
    /// <summary>Gets a value indicating whether the text has an <c>Intent</c> section.</summary>
    public bool Found => Line > 0;
}

/// <summary>
/// Reads the intents of a Markdown text, such as a pull request description. It is the one parser of the format of
/// <c>docs/intent.md</c> section 3, which both <c>vibecheck intent</c> and the review use.
/// </summary>
public static partial class IntentReader
{
    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^( {0,3})([-*+]|\d{1,9}[.)])(?:[ \t]+|$)")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@"^ {0,3}(```|~~~)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^(\*\*|__)(.+?)\1")]
    private static partial Regex Bold();

    [GeneratedRegex(@"^[*_]*(why|instead of)[*_]*[ \t]*:[*_]*[ \t]*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Label();

    [GeneratedRegex(@"^(?:global::)?[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex TypeName();

    /// <summary>
    /// Returns the intents of the first section of a Markdown text whose heading is the word <c>Intent</c>, at any
    /// level. The section ends at the next heading of the same or a higher level. Each top-level list item in it is one
    /// intent, and an item with no claim is reported as a problem and skipped.
    /// </summary>
    public static ParsedIntents Parse(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var (start, end) = FindSection(lines);
        if (start < 0) return new ParsedIntents(0, [], []);

        var intents = new List<Intent>();
        var problems = new List<IntentProblem>();
        foreach (var item in Items(lines, start + 1, end))
        {
            if (ReadItem(item) is { } intent) intents.Add(intent);
            else problems.Add(new IntentProblem(item.Line, "the item has no claim"));
        }
        return new ParsedIntents(start + 1, intents, problems);
    }

    /// <summary>
    /// Returns the anchor that an inline code span names, or <see langword="null"/> when the span is none of a node, an
    /// edge, a file or a type, and so stays text. The arrow of an edge may be written <c>→</c> or <c>-&gt;</c>.
    /// </summary>
    public static IntentAnchor? Anchor(string span)
    {
        var text = span.Trim();
        var arrowed = text.Replace("->", "→", StringComparison.Ordinal);
        if (arrowed.Contains('→'))
        {
            var ends = arrowed.Split('→', StringSplitOptions.TrimEntries);
            return ends.Length == 2 && ends.All(IsEnd) ? new IntentAnchor(text, IntentAnchorKind.Edge, ends[0], ends[1]) : null;
        }
        if (text.Contains('/') || text.Contains('\\') || text.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var path = text.Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            return path.Length == 0 ? null : new IntentAnchor(text, IntentAnchorKind.File, path);
        }
        if (Identifiers.IsValid(text)) return new IntentAnchor(text, IntentAnchorKind.Node, text);
        if (TypeName().IsMatch(text)) return new IntentAnchor(text, IntentAnchorKind.Type, StripGlobal(text));
        return null;
    }

    /// <summary>Gets a value indicating whether a string can be an end of an edge: a node identifier or a type name.</summary>
    private static bool IsEnd(string end) => Identifiers.IsValid(end) || TypeName().IsMatch(end);

    private static string StripGlobal(string name) => name.StartsWith("global::", StringComparison.Ordinal) ? name["global::".Length..] : name;

    /// <summary>
    /// Returns the index of the first <c>Intent</c> heading and the index of the line after the section, or -1 for the
    /// heading when there is none. Lines inside fenced code blocks are never headings.
    /// </summary>
    private static (int Start, int End) FindSection(string[] lines)
    {
        var start = -1;
        var level = 0;
        string? fence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Fence().Match(lines[i]) is { Success: true } f)
            {
                if (fence is null) fence = f.Groups[1].Value;
                else if (f.Groups[1].Value == fence) fence = null;
                continue;
            }
            if (fence is not null || Heading().Match(lines[i]) is not { Success: true } h) continue;
            var depth = h.Groups[1].Length;
            if (start >= 0)
            {
                if (depth <= level) return (start, i);
                continue;
            }
            if (h.Groups[2].Value.Trim().TrimEnd(':').Trim().Equals("Intent", StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                level = depth;
            }
        }
        return (start, lines.Length);
    }

    /// <summary>Represents the lines of one top-level list item, with the marker and the item's indent removed.</summary>
    private sealed record Item(int Line, List<string> Lines);

    /// <summary>
    /// Returns the top-level list items between two lines. An item continues over lines indented further than its
    /// marker, over blank lines, and over unindented lines that directly follow its text. Text outside items is ignored.
    /// </summary>
    private static IEnumerable<Item> Items(string[] lines, int start, int end)
    {
        Item? current = null;
        var (marker, indent) = (0, 0);
        var afterBlank = false;
        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                current?.Lines.Add("");
                afterBlank = true;
                continue;
            }
            var leading = line.Length - line.TrimStart(' ').Length;
            if (current is not null && leading > marker)
            {
                current.Lines.Add(line[Math.Min(leading, indent)..]);
            }
            else if (!ThematicBreak().IsMatch(line) && ListMarker().Match(line) is { Success: true } m)
            {
                if (current is not null) yield return current;
                (marker, indent) = (m.Groups[1].Length, m.Length);
                current = new Item(i + 1, [line[m.Length..]]);
            }
            else if (current is not null && !afterBlank)
            {
                current.Lines.Add(line.Trim());
            }
            else if (current is not null)
            {
                yield return current;
                current = null;
            }
            afterBlank = false;
        }
        if (current is not null) yield return current;
    }

    /// <summary>Returns the intent that a list item states, or <see langword="null"/> when the item has no claim.</summary>
    private static Intent? ReadItem(Item item)
    {
        var first = item.Lines[0].Trim();
        string claim, rest;
        if (Bold().Match(first) is { Success: true } bold)
        {
            claim = bold.Groups[2].Value.Trim();
            rest = first[bold.Length..];
        }
        else
        {
            (claim, rest) = SplitClaim(first);
        }
        if (claim.Length == 0) return null;

        var anchors = new List<IntentAnchor>();
        var leftover = new StringBuilder();
        foreach (var piece in Spans(rest))
        {
            if (piece.Code && Anchor(piece.Text) is { } anchor) anchors.Add(anchor);
            else if (piece.Code) leftover.Append(' ').Append(piece.Raw);
            else if (piece.Text.Trim(' ', '\t', ',').Length > 0) leftover.Append(' ').Append(piece.Text.Trim());
        }

        var fields = new Dictionary<string, StringBuilder> { ["note"] = new(), ["why"] = new(), ["instead of"] = new() };
        fields["note"].Append(leftover.ToString().Trim(' ', ','));
        var field = "note";
        foreach (var line in item.Lines.Skip(1))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                field = "note";
                if (fields["note"].Length > 0) fields["note"].Append('\n');
                continue;
            }
            if (Label().Match(text) is { Success: true } label)
            {
                field = label.Groups[1].Value.ToLowerInvariant();
                text = label.Groups[2].Value.Trim();
                if (text.Length == 0) continue;
            }
            var target = fields[field];
            // A nested list item starts its own line, and other text continues the line before it, as Markdown renders it.
            if (target.Length > 0 && target[^1] != '\n') target.Append(ListMarker().IsMatch(text) ? '\n' : ' ');
            target.Append(text);
        }
        return new Intent(item.Line, claim, anchors, Value(fields["why"]), Value(fields["instead of"]), Value(fields["note"]));
    }

    private static string? Value(StringBuilder text) => text.ToString().Trim() is { Length: > 0 } value ? value : null;

    /// <summary>
    /// Splits the first line of an item without a bold claim into its claim and the rest. The claim is the first
    /// sentence, ending at a full stop, a question mark or an exclamation mark outside code. A line with no such end
    /// takes the anchors from the run of code spans that ends it, and the claim is the text before them.
    /// </summary>
    private static (string Claim, string Remainder) SplitClaim(string line)
    {
        var pieces = Spans(line).ToList();
        var offset = 0;
        foreach (var piece in pieces)
        {
            if (!piece.Code)
            {
                for (var i = 0; i < piece.Text.Length; i++)
                {
                    if (piece.Text[i] is not ('.' or '?' or '!')) continue;
                    var at = offset + i + 1;
                    if (at == line.Length || char.IsWhiteSpace(line[at])) return (line[..at].Trim(), line[at..]);
                }
            }
            offset += piece.Raw.Length;
        }

        var cut = line.Length;
        for (var i = pieces.Count - 1; i >= 0; i--)
        {
            var piece = pieces[i];
            if (piece.Code ? Anchor(piece.Text) is null : piece.Text.Trim(' ', '\t', ',').Length > 0) break;
            cut -= piece.Raw.Length;
        }
        return (line[..cut].Trim(), line[cut..]);
    }

    /// <summary>Represents a piece of a line of Markdown: text, or the content of an inline code span.</summary>
    /// <param name="Code">A value indicating whether the piece is a code span.</param>
    /// <param name="Text">The piece's text, or the span's content without its backticks.</param>
    /// <param name="Raw">The piece as it appears in the line.</param>
    private sealed record Piece(bool Code, string Text, string Raw);

    /// <summary>
    /// Returns the pieces of a line of Markdown. A code span opens and closes with runs of the same number of backticks,
    /// and a run with no match is text.
    /// </summary>
    private static IEnumerable<Piece> Spans(string line)
    {
        var text = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] != '`')
            {
                text.Append(line[i++]);
                continue;
            }
            var run = 0;
            while (i + run < line.Length && line[i + run] == '`') run++;
            var close = FindRun(line, i + run, run);
            if (close < 0)
            {
                text.Append(line, i, run);
                i += run;
                continue;
            }
            if (text.Length > 0) { yield return new Piece(false, text.ToString(), text.ToString()); text.Clear(); }
            yield return new Piece(true, line[(i + run)..close], line[i..(close + run)]);
            i = close + run;
        }
        if (text.Length > 0) yield return new Piece(false, text.ToString(), text.ToString());
    }

    /// <summary>Returns the index of the next run of exactly the given number of backticks at or after a position, or -1.</summary>
    private static int FindRun(string line, int from, int length)
    {
        for (var i = from; i < line.Length;)
        {
            if (line[i] != '`') { i++; continue; }
            var run = 0;
            while (i + run < line.Length && line[i + run] == '`') run++;
            if (run == length) return i;
            i += run;
        }
        return -1;
    }
}
