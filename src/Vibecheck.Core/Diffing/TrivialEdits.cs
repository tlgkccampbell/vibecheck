using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Vibecheck.Diffing;

/// <summary>
/// Decides whether an edit to a C# file can change the compiled program. An edit is trivial when the code on both sides
/// is the same sequence of tokens, so that it touches only comments, documentation comments, whitespace, line breaks,
/// <c>#region</c> lines or <c>#pragma</c> lines. Any other directive and any code an <c>#if</c> leaves out count as
/// code, since they decide what is compiled. Moving code to other lines still changes line numbers, which reach the
/// program only through caller-information attributes and stack traces.
/// </summary>
public static partial class TrivialEdits
{
    /// <summary>Returns a value indicating whether a file name is a C# source file, the only kind this class reads.</summary>
    public static bool Applies(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the hunks of a file with each hunk, and the file, marked trivial when its edit is trivial.</summary>
    /// <param name="file">The hunks of one file, as <see cref="GitHunks.Parse"/> returns them.</param>
    /// <param name="baseText">The whole file at the base revision.</param>
    /// <param name="headText">The whole file at the head revision.</param>
    public static FileHunks Mark(FileHunks file, string baseText, string headText)
    {
        var before = Significant(baseText);
        var after = Significant(headText);
        var hunks = file.Hunks.Select(h =>
        {
            if (!h.TryRanges(out var old, out var now)) return h;
            return Same(Within(before, old), Within(after, now)) ? h with { Trivial = true } : h;
        }).ToList();
        // The whole file is compared on its own, so the file's mark never rests on how git cut the hunks.
        var trivial = Same(before, after);
        return file with { Hunks = hunks, Trivial = trivial ? true : null };
    }

    /// <summary>One token, directive or piece of excluded code, with the first and last lines it covers.</summary>
    private readonly record struct Item(int First, int Last, string Key);

    /// <summary>Returns the parts of a file that the compiler reads, in order.</summary>
    private static List<Item> Significant(string text)
    {
        var source = SourceText.From(text);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var items = new List<Item>();
        Item At(TextSpan span, string key) =>
            new(source.Lines.GetLineFromPosition(span.Start).LineNumber, source.Lines.GetLineFromPosition(Math.Max(span.Start, span.End - 1)).LineNumber, key);
        foreach (var token in root.DescendantTokens())
            if (!token.IsKind(SyntaxKind.EndOfFileToken)) items.Add(At(token.Span, token.RawKind + ":" + token.Text));
        foreach (var trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                items.Add(At(trivia.Span, "disabled:" + Spaces().Replace(trivia.ToString().Trim(), " ")));
            else if (trivia.IsDirective && !trivia.IsKind(SyntaxKind.RegionDirectiveTrivia) && !trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia)
                && !trivia.IsKind(SyntaxKind.PragmaWarningDirectiveTrivia))
                items.Add(At(trivia.Span, "directive:" + string.Join(" ", trivia.GetStructure()!.DescendantTokens().Select(t => t.Text))));
        }
        // Tokens and trivia come from two walks, so they are put back in the order they appear.
        return items.OrderBy(i => i.First).ThenBy(i => i.Last).ToList();
    }

    /// <summary>Returns the items that touch a range of zero-based lines, given as its first line and its count.</summary>
    private static List<Item> Within(List<Item> items, (int Start, int Count) range) =>
        range.Count == 0 ? [] : items.Where(i => i.Last >= range.Start && i.First < range.Start + range.Count).ToList();

    private static bool Same(List<Item> a, List<Item> b) => a.Select(i => i.Key).SequenceEqual(b.Select(i => i.Key), StringComparer.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
