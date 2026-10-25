using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vibecheck.Checking;

namespace Vibecheck.Diffing;

/// <summary>
/// A test signal: a fact about what a change did to a test method, worded as a fact and never as a judgment of the test.
/// The kind is one of the constants of this record, and the method is the test method's symbol at head, or at base
/// when it was removed. The declarations are the changed declarations the method references, at either end and in
/// either way; a signal touches changed code when there are any. See <c>docs/tests.md</c> section 6.
/// </summary>
public sealed record TestSignal(string Kind, string Method, List<TestedDeclaration> Touches)
{
    /// <summary>A test method at base is not at head, and no renamed or moved declaration accounts for it.</summary>
    public const string Removed = "removed";

    /// <summary>A test method that was not skipped at base is skipped at head.</summary>
    public const string Skipped = "skipped";

    /// <summary>A test method at both ends has fewer assertions at head than at base.</summary>
    public const string AssertionRemoved = "assertion-removed";

    /// <summary>A changed line of a test method at both ends holds an assertion call at either end.</summary>
    public const string AssertionChanged = "assertion-changed";

    /// <summary>An assertion changed, and every changed assertion line pairs with one that differs from it only in literals.</summary>
    public const string ExpectedChanged = "expected-changed";
}

/// <summary>Finds the test signals of a changeset's hunks. See <c>docs/tests.md</c> section 6.1.</summary>
public static class TestSignals
{
    /// <summary>One changed line of a hunk: its marker, its one-based line number at the end it belongs to, and its text.</summary>
    private readonly record struct Line(bool Removed, int Number, string Text);

    /// <summary>A hunk with the paths of its file at each end and its changed lines.</summary>
    private sealed record Located(string BasePath, string HeadPath, string Path, int Index, List<Line> Lines);

    /// <summary>
    /// Returns the signals of each hunk, keyed by the file's path in the changeset and the hunk's index. Each signal is on
    /// the hunk that shows it: a removed method's on the hunk that removes it, a changed assertion's on the hunk that
    /// changes it, and a skip or a removed assertion on the first hunk that changes the method.
    /// </summary>
    /// <param name="baseFacts">The facts at base.</param>
    /// <param name="headFacts">The facts at head.</param>
    /// <param name="files">The changed files, which give a renamed file's path at base.</param>
    /// <param name="hunks">The hunks of every changed file.</param>
    /// <param name="touches">Returns the changed declarations a test method references, given its symbol at base and at head, either of which may be absent.</param>
    public static Dictionary<(string Path, int Hunk), List<TestSignal>> Find(Facts baseFacts, Facts headFacts, IReadOnlyList<FileChange> files,
        IReadOnlyList<FileHunks> hunks, Func<string?, string?, List<TestedDeclaration>> touches)
    {
        var result = new Dictionary<(string, int), List<TestSignal>>();
        var baseMethods = baseFacts.Tests.Types.SelectMany(t => t.Methods).DistinctBy(m => m.Symbol).ToDictionary(m => m.Symbol, StringComparer.Ordinal);
        var headMethods = headFacts.Tests.Types.SelectMany(t => t.Methods).DistinctBy(m => m.Symbol).ToDictionary(m => m.Symbol, StringComparer.Ordinal);
        if (baseMethods.Count == 0) return result;
        // A test method renamed or moved unchanged is the same method at both ends.
        var paired = DeclarationDiff.Diff(baseFacts.Tests.Declarations, headFacts.Tests.Declarations)
            .Where(c => c.Change is "renamed" or "moved" && c.Previous is not null).ToDictionary(c => c.Previous!, c => c.Symbol, StringComparer.Ordinal);

        var previous = files.Where(f => f.PreviousPath is not null).ToDictionary(f => f.Path, f => f.PreviousPath!, StringComparer.Ordinal);
        var located = new List<Located>();
        foreach (var file in hunks)
        {
            for (var i = 0; i < file.Hunks.Count; i++)
            {
                if (!file.Hunks[i].TryRanges(out var old, out var now)) continue;
                located.Add(new Located(previous.GetValueOrDefault(file.Path, file.Path), file.Path, file.Path, i, ChangedLines(file.Hunks[i], old.Start + 1, now.Start + 1)));
            }
        }
        // The lines of a hunk that lie in a method at base or at head.
        static List<Line> In(Located h, TestMethodFact? m, bool removed) =>
            m is null || (removed ? h.BasePath : h.HeadPath) != m.File ? [] : h.Lines.Where(l => l.Removed == removed && m.First <= l.Number && l.Number <= m.Last).ToList();
        void Add(Located h, TestSignal signal)
        {
            if (!result.TryGetValue((h.Path, h.Index), out var list)) result[(h.Path, h.Index)] = list = [];
            list.Add(signal);
        }

        foreach (var b in baseMethods.Values.OrderBy(m => m.Symbol, StringComparer.Ordinal))
        {
            var headSymbol = headMethods.ContainsKey(b.Symbol) ? b.Symbol : paired.GetValueOrDefault(b.Symbol);
            if (headSymbol is null || !headMethods.TryGetValue(headSymbol, out var h))
            {
                if (located.FirstOrDefault(x => In(x, b, removed: true).Count > 0) is { } removing)
                    Add(removing, new TestSignal(TestSignal.Removed, b.Symbol, touches(b.Symbol, null)));
                continue;
            }
            var touching = located.Where(x => In(x, b, removed: true).Count + In(x, h, removed: false).Count > 0).ToList();
            if (touching.Count == 0) continue;
            var touched = touches(b.Symbol, h.Symbol);
            if (!b.Skipped && h.Skipped) Add(touching[0], new TestSignal(TestSignal.Skipped, h.Symbol, touched));
            if (h.Assertions.Count < b.Assertions.Count) Add(touching[0], new TestSignal(TestSignal.AssertionRemoved, h.Symbol, touched));
            foreach (var x in touching)
            {
                var removed = In(x, b, removed: true);
                var added = In(x, h, removed: false);
                bool Asserts(Line l) => (l.Removed ? b : h).Assertions.Any(a => a.Holds(l.Number));
                if (!removed.Any(Asserts) && !added.Any(Asserts)) continue;
                // The changed lines pair in order, as a rename's do, when the two sides have as many; an assertion whose
                // every changed line differs from its pair only in literals changed its expected value.
                var expected = removed.Count == added.Count && Enumerable.Range(0, removed.Count)
                    .Where(k => Asserts(removed[k]) || Asserts(added[k])).All(k => LiteralsOnly(removed[k].Text, added[k].Text));
                Add(x, new TestSignal(expected ? TestSignal.ExpectedChanged : TestSignal.AssertionChanged, h.Symbol, touched));
            }
        }
        return result;
    }

    /// <summary>Returns a value indicating whether two lines are the same tokens but for at least one literal that differs.</summary>
    internal static bool LiteralsOnly(string a, string b)
    {
        var x = SyntaxFactory.ParseTokens(a).Where(t => !t.IsKind(SyntaxKind.EndOfFileToken)).ToList();
        var y = SyntaxFactory.ParseTokens(b).Where(t => !t.IsKind(SyntaxKind.EndOfFileToken)).ToList();
        if (x.Count != y.Count) return false;
        var differs = false;
        for (var k = 0; k < x.Count; k++)
        {
            if (x[k].Text == y[k].Text) continue;
            if (!IsLiteral(x[k]) || !IsLiteral(y[k])) return false;
            differs = true;
        }
        return differs;
    }

    private static bool IsLiteral(SyntaxToken token) => token.Kind() is SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken
        or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken;

    /// <summary>Returns a hunk's changed lines, numbering each removed line at base and each added line at head.</summary>
    private static List<Line> ChangedLines(Hunk hunk, int baseLine, int headLine)
    {
        var result = new List<Line>();
        foreach (var line in hunk.Lines)
        {
            switch (line[0])
            {
                case "-": result.Add(new Line(true, baseLine++, line[1])); break;
                case "+": result.Add(new Line(false, headLine++, line[1])); break;
                default: baseLine++; headLine++; break;
            }
        }
        return result;
    }
}
