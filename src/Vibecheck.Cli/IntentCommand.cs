using Vibecheck.Diffing;
using Vibecheck.Intents;
using Vibecheck.Model;

namespace Vibecheck.Cli;

/// <summary>
/// Checks the <c>Intent</c> section of a draft pull request description for <c>vibecheck intent</c>, without building or
/// analyzing the solution. It reports each intent with its anchors, and whether each anchor names a node of the map or
/// a file of the working tree. See <c>docs/intent.md</c> section 7.3.
/// </summary>
internal static class IntentCommand
{
    /// <summary>
    /// Checks the intents of a Markdown text, writes the report, and returns the exit code. The code is 1 when the text
    /// has no <c>Intent</c> section or an item has no claim, and 0 otherwise. Unknown anchors are warnings.
    /// </summary>
    /// <param name="markdown">The text to check, such as a draft pull request description.</param>
    /// <param name="map">The repository's map, or <see langword="null"/> when it has none.</param>
    /// <param name="top">The repository's top directory, which file anchors are relative to.</param>
    /// <param name="baseRevision">The revision to report changed files against, or <see langword="null"/> to report none.</param>
    /// <param name="output">The writer for the report.</param>
    /// <param name="error">The writer for the warnings.</param>
    public static int Run(string markdown, ArchitectureMap? map, string top, string? baseRevision, TextWriter output, TextWriter error)
    {
        var parsed = IntentReader.Parse(markdown);
        if (!parsed.Found)
        {
            output.WriteLine("no Intent section: the text has no heading that is the word Intent");
            return 1;
        }

        var changed = baseRevision is null ? null : ChangedFiles(top, baseRevision);
        var warnings = new List<string>();
        foreach (var intent in parsed.Intents)
        {
            output.WriteLine($"line {intent.Line}: {intent.Claim}");
            if (intent.Anchors.Count == 0) output.WriteLine("  no anchors, so a review lists the intent but attaches it to nothing");
            foreach (var anchor in intent.Anchors)
            {
                var (state, warning) = Describe(anchor, map, top, baseRevision, changed);
                output.WriteLine($"  {anchor.Kind.ToString().ToLowerInvariant()} {anchor.Text}: {state}");
                if (warning is not null) warnings.Add($"line {intent.Line}: `{anchor.Text}` {warning}");
            }
            if (intent.Why is null) warnings.Add($"line {intent.Line}: the intent has no Why:");
        }
        foreach (var w in warnings) error.WriteLine("warning: " + w);
        foreach (var p in parsed.Problems) output.WriteLine($"line {p.Line}: {p.Message}");
        if (parsed.Problems.Count > 0)
        {
            output.WriteLine($"{parsed.Problems.Count} failure(s)");
            return 1;
        }
        var anchors = parsed.Intents.Sum(i => i.Anchors.Count);
        output.WriteLine($"ok: {parsed.Intents.Count} intent(s) with {anchors} anchor(s) in the section at line {parsed.Line}, {warnings.Count} warning(s)");
        return 0;
    }

    /// <summary>
    /// Returns what an anchor names as far as the map and the working tree tell, and a warning when it names nothing.
    /// Types are never checked, since only the analysis knows them.
    /// </summary>
    private static (string State, string? Warning) Describe(IntentAnchor anchor, ArchitectureMap? map, string top, string? baseRevision, IReadOnlySet<string>? changed)
    {
        const string Unchecked = "unchecked, since only a review knows the types";        switch (anchor.Kind)
        {
            case IntentAnchorKind.Node:
                return Node(anchor.Name, map) is { } missing ? ("unknown", missing) : (map!.Externals.ContainsKey(anchor.Name) ? "an external of the map" : "a node of the map", null);
            case IntentAnchorKind.Edge:
                var nodes = new[] { anchor.Name, anchor.To! }.Where(Identifiers.IsValid).ToList();
                foreach (var node in nodes)
                {
                    if (Node(node, map) is { } warning) return ("unknown", $"has an end, {node}, that {warning}");
                }
                return nodes.Count switch
                {
                    2 => ("both ends are in the map", null),
                    1 => ("the node end is in the map, and the type end is " + Unchecked, null),
                    _ => (Unchecked, null),
                };
            case IntentAnchorKind.File:
                var exists = File.Exists(Path.Combine(top, anchor.Name));
                if (changed is null) return exists ? ("exists", null) : ("unknown", "names no file in the working tree");
                var isChanged = changed.Contains(anchor.Name);
                if (!exists) return isChanged ? ($"deleted since {baseRevision}", null) : ("unknown", "names no file in the working tree");
                return (isChanged ? $"exists, changed since {baseRevision}" : $"exists, unchanged since {baseRevision}", null);
            default:
                return (Unchecked, null);
        }
    }

    /// <summary>Returns a warning when an identifier names no node or external of the map, or <see langword="null"/> when it names one.</summary>
    private static string? Node(string id, ArchitectureMap? map) =>
        map is null ? "names a node, but there is no map; name a file or a type instead"
        : map.Exists(id) && id != Identifiers.Root ? null
        : "names no node or external of the map";

    /// <summary>
    /// Returns the files that differ between a revision and the working tree, from <c>git diff --name-only</c>, with the
    /// untracked files that git does not ignore, since a draft's new files are often not yet added.
    /// </summary>
    private static HashSet<string> ChangedFiles(string top, string revision)
    {
        var diff = Git.Run(top, "-c", "core.quotePath=false", "diff", "--name-only", revision, "--");
        var untracked = Git.Run(top, "-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard");
        return diff.Split('\n').Concat(untracked.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
    }
}
