using Vibecheck.Analysis;
using Vibecheck.Checking;

namespace Vibecheck.Diffing;

/// <summary>
/// Finds the decisions among a changeset's hunks and the consequences that follow from them, as
/// <c>docs/attention.md</c> section 4 describes. A decision is a hunk whose changed lines touch a declaration that
/// was added, removed, reshaped, renamed or moved. A consequence is a hunk whose every changed line is blank, is a
/// comment, or references at its own end a declaration that a decision changed. A hunk with a changed line that
/// nothing explains is neither.
/// </summary>
public static class Clustering
{
    /// <summary>One changed line of a hunk: its marker, its one-based line number at the end it belongs to, and its text.</summary>
    private readonly record struct Line(bool Removed, int Number, string Text);

    /// <summary>A hunk with the paths of its file at each end, its changed lines, and whether its edit is trivial.</summary>
    private sealed record Located(FileHunks File, int Index, string BasePath, string HeadPath, List<Line> Changed, bool Trivial);

    /// <summary>
    /// Returns the decisions among the hunks, and the hunks with each consequence marked with the decisions it follows
    /// from. A trivial hunk is never a decision. It is a consequence when its lines follow from one, as a rename
    /// consequence's do, and a rename consequence's reason then names the rename's decision.
    /// </summary>
    /// <param name="baseFacts">The facts at base, whose declarations and sites the removed lines are read against.</param>
    /// <param name="headFacts">The facts at head, whose declarations and sites the added lines are read against.</param>
    /// <param name="files">The changed files, which give a renamed file's path at base.</param>
    /// <param name="hunks">The hunks of every changed file.</param>
    /// <param name="changes">The declaration diff of the two ends, or <see langword="null"/> to compute it here.</param>
    public static (List<Decision> Decisions, List<FileHunks> Hunks) Apply(Facts baseFacts, Facts headFacts, IReadOnlyList<FileChange> files, List<FileHunks> hunks,
        IReadOnlyList<DeclarationChange>? changes = null)
    {
        changes ??= DeclarationDiff.Diff(baseFacts.Declarations, headFacts.Declarations);
        if (changes.Count == 0 || hunks.Count == 0) return ([], hunks);
        var decisive = changes.Where(c => c.Change != "rewritten").ToList();
        var atBase = decisive.Where(c => c.BaseSymbol is not null).ToDictionary(c => c.BaseSymbol!, StringComparer.Ordinal);
        var atHead = decisive.Where(c => c.HeadSymbol is not null).ToDictionary(c => c.HeadSymbol!, StringComparer.Ordinal);
        var baseDeclarations = ByFile(baseFacts.Declarations);
        var headDeclarations = ByFile(headFacts.Declarations);
        var baseSites = SitesOf(baseFacts);
        var headSites = SitesOf(headFacts);
        var baseCounts = baseSites.Values.SelectMany(s => s).CountBy(s => s, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);
        var headCounts = headSites.Values.SelectMany(s => s).CountBy(s => s, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);
        var previous = files.Where(f => f.PreviousPath is not null).ToDictionary(f => f.Path, f => f.PreviousPath!, StringComparer.Ordinal);

        var located = new List<Located>();
        foreach (var file in hunks)
        {
            for (var i = 0; i < file.Hunks.Count; i++)
            {
                var hunk = file.Hunks[i];
                if (!hunk.TryRanges(out var old, out var now)) continue;
                located.Add(new Located(file, i, previous.GetValueOrDefault(file.Path, file.Path), file.Path, ChangedLines(hunk, old.Start + 1, now.Start + 1), hunk.Trivial == true));
            }
        }

        // A hunk is a decision when one of its changed lines lies in a changed declaration at that line's end: anywhere
        // in one that was added or removed, and in the lines before the body of one whose shape or name changed.
        var decisions = new List<Decision>();
        var bySymbol = (Base: new Dictionary<string, List<string>>(StringComparer.Ordinal), Head: new Dictionary<string, List<string>>(StringComparer.Ordinal));
        var decided = new HashSet<Located>();
        foreach (var h in located)
        {
            if (h.Trivial) continue;
            var held = new Dictionary<string, (DeclarationChange Change, Declaration Declaration)>(StringComparer.Ordinal);
            foreach (var line in h.Changed)
            {
                var (declarations, changed) = line.Removed ? (baseDeclarations, atBase) : (headDeclarations, atHead);
                foreach (var d in declarations.GetValueOrDefault(line.Removed ? h.BasePath : h.HeadPath, []))
                {
                    if (!changed.TryGetValue(d.Symbol, out var c) || !Covers(d, c, line.Number)) continue;
                    held.TryAdd(c.Symbol, (c, d));
                }
            }
            if (held.Count == 0) continue;
            var id = h.File.Path + "#" + h.Index;
            var listed = held.Values.OrderBy(x => x.Change.Symbol, StringComparer.Ordinal).Select(x => new DecisionDeclaration(
                x.Change.Symbol, x.Change.Change, x.Change.Kind, x.Declaration.Accessibility,
                x.Change.BaseSymbol is { } b ? baseCounts.GetValueOrDefault(b) : 0,
                x.Change.HeadSymbol is { } s ? headCounts.GetValueOrDefault(s) : 0,
                x.Change.Previous)).ToList();
            decisions.Add(new Decision(id, h.File.Path, h.Index, held.Values.Any(x => x.Declaration.Exposed), listed, []));
            decided.Add(h);
            foreach (var (c, _) in held.Values)
            {
                if (c.BaseSymbol is { } b) Add(bySymbol.Base, b, id);
                if (c.HeadSymbol is { } s) Add(bySymbol.Head, s, id);
            }
        }

        // A hunk is a consequence when every changed line is trivial or references a declaration that a decision
        // changed, at the line's own end, and at least one does.
        var follows = new Dictionary<(string File, int Index), List<string>>();
        var whole = new HashSet<(string File, int Index, string Decision)>();
        var byId = decisions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var order = decisions.Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        foreach (var h in located)
        {
            if (decided.Contains(h)) continue;
            var lines = new Dictionary<string, int>(StringComparer.Ordinal);
            var explained = true;
            var significant = 0;
            foreach (var line in h.Changed)
            {
                if (IsTrivial(line.Text)) continue;
                significant++;
                var (sites, decidedBy) = line.Removed ? (baseSites, bySymbol.Base) : (headSites, bySymbol.Head);
                var ids = sites.GetValueOrDefault(((line.Removed ? h.BasePath : h.HeadPath), line.Number), [])
                    .SelectMany(s => decidedBy.GetValueOrDefault(s, [])).Distinct(StringComparer.Ordinal).ToList();
                if (ids.Count == 0) { explained = false; break; }
                foreach (var id in ids) lines[id] = lines.GetValueOrDefault(id) + 1;
            }
            if (!explained || lines.Count == 0) continue;
            var ordered = lines.OrderByDescending(kv => kv.Value).ThenBy(kv => order[kv.Key]).Select(kv => kv.Key).ToList();
            follows[(h.File.Path, h.Index)] = ordered;
            foreach (var id in ordered)
            {
                byId[id].Consequences.Add(new Consequence(h.File.Path, h.Index, lines[id]));
                if (lines[id] == significant) whole.Add((h.File.Path, h.Index, id));
            }
        }

        // A rename consequence's reason names the decision that holds the rename, so that the viewer can link to it.
        var renamedBy = decisions.SelectMany(d => d.Declarations.Where(x => x.Change == "renamed").Select(x => (x.Symbol, d.Id)))
            .GroupBy(x => x.Symbol, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        EditReason? Linked(EditReason? reason) =>
            reason is { Kind: EditReason.Rename, Decision: null, Symbol: { } symbol } && renamedBy.TryGetValue(symbol, out var id) ? reason with { Decision = id } : reason;
        var marked = hunks.Select(f => f with
        {
            Hunks = f.Hunks.Select((hunk, i) => (follows.TryGetValue((f.Path, i), out var ids) ? hunk with { Follows = ids } : hunk) with { Reason = Linked(hunk.Reason) }).ToList(),
            Reason = Linked(f.Reason),
        }).ToList();
        // A decision's mark covers a file only when the decision alone explains every edit to it, so that marking it
        // never marks lines that follow from a decision the reviewer has not read.
        var byPath = marked.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var result = decisions.Select(d => d with
        {
            Marks = d.Consequences.Select(c => c.File).Distinct(StringComparer.Ordinal)
                .Where(path => byPath[path].Hunks.Select((hunk, i) => hunk.Trivial == true || whole.Contains((path, i, d.Id))).All(x => x)).ToList(),
        }).ToList();
        return (result, marked);
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string id)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(id)) list.Add(id);
    }

    /// <summary>Returns a value indicating whether a changed line touches a changed declaration in the part of it that the change is about.</summary>
    private static bool Covers(Declaration d, DeclarationChange change, int line) => change.Change is "added" or "removed"
        ? line >= d.FirstLine && line <= d.LastLine
        : line >= d.FirstLine && line <= (d.BodyLine ?? d.LastLine);

    /// <summary>Returns a value indicating whether a changed line is blank or holds only a comment, which explains itself.</summary>
    private static bool IsTrivial(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) return true;
        return t.StartsWith("/*", StringComparison.Ordinal) && t.IndexOf("*/", StringComparison.Ordinal) == t.Length - 2;
    }

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

    private static Dictionary<string, List<Declaration>> ByFile(IEnumerable<Declaration> declarations) =>
        declarations.GroupBy(d => d.File, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    /// <summary>Returns the symbols referenced on each line of each file at one end, from its dependencies and its sites together.</summary>
    private static Dictionary<(string File, int Line), List<string>> SitesOf(Facts facts)
    {
        var result = new Dictionary<(string, int), List<string>>();
        void Add(string file, int line, string symbol)
        {
            if (!result.TryGetValue((file, line), out var list)) result[(file, line)] = list = [];
            list.Add(symbol);
        }
        foreach (var d in facts.Dependencies) Add(d.File, d.Line, d.Symbol);
        foreach (var s in facts.Sites) Add(s.File, s.Line, s.Symbol);
        return result;
    }
}
