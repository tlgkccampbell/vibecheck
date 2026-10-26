using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vibecheck.Diffing;

namespace Vibecheck.Assist;

/// <summary>Represents one slide of an explainer: a claim, a few sentences, what the viewer shows beside them, and the evidence they rest on.</summary>
/// <param name="Title">A few words that state what the slide claims.</param>
/// <param name="Text">Two to five sentences, with type names in backticks.</param>
/// <param name="Cites">The changed files and type pairs the text rests on, each pair as <see cref="Narrative.PairCite"/> writes it.</param>
/// <param name="Target">What the viewer shows while the slide is current.</param>
public sealed record Slide(string Title, string Text, IReadOnlyList<string> Cites, ExplainerTarget Target);

/// <summary>Represents a step of the reading path's first tier, or a changed block, that no slide of an explainer covers.</summary>
/// <param name="Kind">Either <c>step</c> or <c>block</c>.</param>
/// <param name="Id">The step's <see cref="PathStep.Id"/> or the block's node identifier.</param>
public sealed record ExplainerGap(string Kind, string Id);

/// <summary>
/// Represents an explainer: a linear narrative of a changeset as a run of slides, written by Claude and checked against
/// the changeset's catalog. See <c>docs/explainers.md</c>.
/// </summary>
/// <param name="Slides">The slides that passed their check, in order.</param>
/// <param name="Dropped">The number of slides that failed their check twice, or failed on their one request.</param>
/// <param name="Reasons">Why each dropped slide was dropped, as <c>Slide 'title': reason</c>.</param>
/// <param name="Uncovered">The first-tier steps and changed blocks that no slide covers.</param>
public sealed record Explainer(IReadOnlyList<Slide> Slides, int Dropped, IReadOnlyList<string> Reasons, IReadOnlyList<ExplainerGap> Uncovered);

/// <summary>Holds what an explainer is written from, besides its catalog.</summary>
/// <param name="Commits">The commit messages in the range, as <see cref="NarrativeContext.ReadCommits"/> lists them.</param>
/// <param name="Narratives">The summary and the narratives written so far, which the explainer is told to build on.</param>
public sealed record ExplainerContext(string Commits, IReadOnlyList<Narrative> Narratives)
{
    /// <summary>
    /// Gets the directory Claude reads the code at head in, with the paths of the changeset relative to it, or
    /// <see langword="null"/> to give Claude no tools.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the model that writes the explainer, or <see langword="null"/> for the Claude Code default.</summary>
    public string? Model { get; init; }
}

/// <summary>Stores checked explainers by the request that produced them. <c>ReviewCache</c> is the real one.</summary>
public interface IExplainerCache
{
    /// <summary>Returns the explainer stored under a request key, or <see langword="null"/> when there is none.</summary>
    Explainer? ReadExplainer(string key);

    /// <summary>Stores the explainer a request produced under its key, replacing any stored before.</summary>
    void WriteExplainer(string key, Explainer explainer);
}

/// <summary>
/// Writes the explainer of a changeset with Claude in one request, and checks each slide against the catalog of
/// <see cref="ExplainerPlanner"/>. A slide whose target is not in the catalog, whose brought-out block is not under its
/// level, or whose cite is neither a changed file nor a type pair of a changed edge is rejected. The rejected slides,
/// with the steps and blocks no slide covers, are requested once more together, and a slide that fails again is
/// dropped. See <c>docs/explainers.md</c> section 4.
/// </summary>
public static class ExplainerWriter
{
    /// <summary>
    /// Returns the explainer of a catalog, from the cache when it holds the same request and <paramref name="regenerate"/>
    /// is false, and otherwise from Claude, storing it.
    /// </summary>
    /// <param name="regenerate">A value indicating whether a stored explainer is ignored and written anew.</param>
    /// <exception cref="ClaudeException">Claude failed on the first request, or no slide passed its check.</exception>
    public static async Task<Explainer> WriteAsync(ExplainerCatalog catalog, ExplainerContext context, IClaude claude, IExplainerCache? cache,
        bool regenerate = false, CancellationToken ct = default)
    {
        var prompt = Prompt(catalog, context);
        var key = RequestKey(prompt, context);
        if (!regenerate && cache?.ReadExplainer(key) is { } stored) return stored;

        var first = Check(catalog, Slides(await AskAsync(prompt, Prompts.Read("explainer.schema.json"), context, claude, ct)));
        var slots = first.Select(c => (Checked: c, Current: c.Passed ? c : null, Reason: c.Reason)).ToList();
        var inserted = new List<(int After, SlideCheck Slide)>();
        var failedNew = new List<SlideCheck>();
        var gaps = Gaps(catalog, first.Where(c => c.Passed).ToList());
        if (first.Any(c => !c.Passed) || gaps.Count > 0)
        {
            try
            {
                var answer = await AskAsync(prompt + RetrySection(catalog, first, gaps), Prompts.Read("explainer-retry.schema.json"), context, claude, ct);
                var replaced = new HashSet<int>();
                foreach (var (slide, replaces, after) in Check(catalog, Slides(answer)).Zip(Slides(answer), (c, raw) => (c, Int(raw, "replaces"), Int(raw, "after"))))
                {
                    if (replaces >= 1 && replaces <= slots.Count && replaced.Add(replaces))
                    {
                        var slot = slots[replaces - 1];
                        // A slide that passed the first time stays when its revision fails; one that failed is dropped.
                        if (slide.Passed) slots[replaces - 1] = slot with { Current = slide };
                        else if (slot.Current is null) slots[replaces - 1] = slot with { Reason = slide.Reason };
                    }
                    else if (slide.Passed) inserted.Add((Math.Clamp(after, 0, slots.Count), slide));
                    else failedNew.Add(slide);
                }
            }
            catch (ClaudeException)
            {
                // The slides that passed the first time stand, and the rest are dropped.
            }
        }

        var slides = new List<SlideCheck>();
        for (var i = 0; i <= slots.Count; i++)
        {
            slides.AddRange(inserted.Where(x => x.After == i).Select(x => x.Slide));
            if (i < slots.Count && slots[i].Current is { } current) slides.Add(current);
        }
        if (slides.Count == 0) throw new ClaudeException("no slide of the answer passed its check");
        var dropped = slots.Where(s => s.Current is null).Select(s => Reason(s.Checked.Title, s.Reason!)).Concat(failedNew.Select(s => Reason(s.Title, s.Reason!))).ToList();
        var explainer = new Explainer(slides.Select(s => s.Slide!).ToList(), dropped.Count, dropped, Gaps(catalog, slides));
        cache?.WriteExplainer(key, explainer);
        return explainer;
    }

    /// <summary>
    /// Returns the key an explainer is stored under: a hash of everything that decides the answer, which is the prompt,
    /// the schemas and the model. The prompt holds the prompt template, so a new version of it is a new key.
    /// </summary>
    internal static string RequestKey(string prompt, ExplainerContext context) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', prompt, Prompts.Read("explainer.schema.json"),
            Prompts.Read("explainer-retry.schema.json"), context.Model ?? ""))))[..24];

    private static Task<JsonElement> AskAsync(string prompt, string schema, ExplainerContext context, IClaude claude, CancellationToken ct) =>
        claude.AskAsync(new ClaudeRequest(prompt, schema)
        {
            Model = context.Model,
            Timeout = NarrativeWriter.RequestTimeout,
            WorkingDirectory = context.WorkingDirectory,
            Tools = context.WorkingDirectory is null ? [] : NarrativeWriter.Tools,
        }, ct);

    private static string Reason(string title, string reason) => $"Slide '{title}': {reason}";

    // ---------- Checking ----------

    /// <summary>Represents one slide of an answer as checked: the slide resolved against the catalog and its entry, or the reason it was rejected.</summary>
    internal sealed record SlideCheck(string Title, Slide? Slide, CatalogEntry? Entry, string? Reason)
    {
        /// <summary>Gets a value indicating whether the slide passed its check.</summary>
        public bool Passed => Slide is not null;
    }

    /// <summary>
    /// Checks the slides of an answer against a catalog, in order. Each slide that passes is resolved to the target the
    /// viewer opens, and each that fails carries the reason.
    /// </summary>
    internal static List<SlideCheck> Check(ExplainerCatalog catalog, IEnumerable<JsonElement> slides)
    {
        var result = new List<SlideCheck>();
        foreach (var s in slides)
        {
            var title = Str(s, "title").Trim();
            var text = Str(s, "text").Trim();
            var target = s.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
            var kind = target.ValueKind == JsonValueKind.Object ? Str(target, "kind") : "";
            var id = target.ValueKind == JsonValueKind.Object ? Str(target, "id").Replace("->", "→", StringComparison.Ordinal) : "";
            var entry = Resolve(catalog, kind, id);
            var cites = Strings(s, "cites").Select(NormalizeCite).Distinct(StringComparer.Ordinal).ToList();
            string? reason = null;
            if (title.Length == 0) reason = "the title is empty";
            else if (text.Length == 0) reason = "the text is empty";
            else if (entry is null) reason = $"its target {(kind.Length > 0 ? kind + " " : "")}{id} is not in the catalog";
            var brought = new List<string>();
            if (reason is null && entry!.Kind == "level" && target.ValueKind == JsonValueKind.Object)
            {
                brought = Strings(target, "blocks").Select(b => b.StartsWith("block:", StringComparison.Ordinal) ? b[6..] : b).Distinct(StringComparer.Ordinal).ToList();
                var foreign = brought.Where(b => !entry.Blocks.Contains(b)).ToList();
                if (foreign.Count > 0) reason = $"it brings out {string.Join(", ", foreign)}, which {(foreign.Count == 1 ? "is not a block" : "are not blocks")} of {entry.Id}";
            }
            if (reason is null)
            {
                var foreign = cites.Where(c => !catalog.CitableFiles.Contains(c) && !catalog.CitablePairs.Contains(c)).ToList();
                if (foreign.Count > 0) reason = $"it cites {string.Join(", ", foreign)}, which {(foreign.Count == 1 ? "is" : "are")} neither a changed file nor a type pair of a changed edge";
            }
            if (reason is not null)
            {
                result.Add(new SlideCheck(title.Length > 0 ? title : "(untitled)", null, null, reason));
                continue;
            }
            var resolved = entry!.Target with { Blocks = brought.Count > 0 ? brought : entry.Target.Blocks, Step = StepOf(catalog, entry.Id) };
            result.Add(new SlideCheck(title, new Slide(title, text, cites, resolved), entry, null));
        }
        return result;
    }

    /// <summary>Returns the catalog entry a slide's target names, by its identifier with or without the kind written before it, or <see langword="null"/>.</summary>
    private static CatalogEntry? Resolve(ExplainerCatalog catalog, string kind, string id)
    {
        foreach (var candidate in new[] { id, kind + ":" + id })
            if (catalog.Entries.TryGetValue(candidate, out var e) && (kind.Length == 0 || e.Kind == kind)) return e;
        return null;
    }

    /// <summary>Returns the identifier of the first step of the catalog's two tiers whose target is an entry, in the path's order, or <see langword="null"/> when none is.</summary>
    private static string? StepOf(ExplainerCatalog catalog, string entry) =>
        catalog.FirstTier.Concat(catalog.SecondTier).FirstOrDefault(s => catalog.StepTargets.TryGetValue(s.Id, out var t) && t == entry)?.Id;

    /// <summary>Returns a cite with a type pair written as <see cref="Narrative.PairCite"/> writes it, whatever arrow or spacing it came with.</summary>
    private static string NormalizeCite(string cite)
    {
        var c = cite.Trim().Replace("->", "→", StringComparison.Ordinal);
        var arrow = c.IndexOf('→');
        return arrow < 0 ? c : Narrative.PairCite(c[..arrow].Trim(), c[(arrow + 1)..].Trim());
    }

    // ---------- Coverage ----------

    /// <summary>
    /// Returns the first-tier steps and changed blocks of a catalog that no slide covers. A step is covered by a slide
    /// that targets it, that targets or cites a file or type pair the step names, or that targets the block a decision
    /// or file step sits under. A block is covered by a slide that targets it or anything inside it, or that brings it
    /// out on a level. See <c>docs/explainers.md</c> section 4.3.
    /// </summary>
    internal static List<ExplainerGap> Gaps(ExplainerCatalog catalog, IReadOnlyList<SlideCheck> slides)
    {
        var gaps = new List<ExplainerGap>();
        foreach (var step in catalog.FirstTier)
            if (!slides.Any(s => Covers(catalog, s, step))) gaps.Add(new ExplainerGap("step", step.Id));
        foreach (var block in catalog.Blocks.Select(b => b.Target.Id))
        {
            static bool Within(string id, string block) => id == block || id.StartsWith(block + ".", StringComparison.Ordinal);
            if (!slides.Any(s => s.Entry!.Inside.Any(i => Within(i, block)) || (s.Slide!.Target.Blocks ?? []).Any(b => Within(b, block))))
                gaps.Add(new ExplainerGap("block", block));
        }
        return gaps;
    }

    /// <summary>Returns a value indicating whether a slide covers a first-tier step, by the rules of <see cref="Gaps"/>.</summary>
    private static bool Covers(ExplainerCatalog catalog, SlideCheck slide, PathStep step)
    {
        var entry = slide.Entry!;
        if (catalog.StepTargets.TryGetValue(step.Id, out var target) && target == entry.Id) return true;
        if (entry.Files.Any(step.Files.Contains) || slide.Slide!.Cites.Any(step.Files.Contains)) return true;
        if (catalog.StepPairs.TryGetValue(step.Id, out var pairs) && pairs.Any(p => entry.Pairs.Contains(p) || slide.Slide!.Cites.Contains(p))) return true;
        return catalog.StepBlocks.TryGetValue(step.Id, out var block) && entry.Kind == "block" && entry.Target.Id == block;
    }

    // ---------- Prompts ----------

    /// <summary>Returns the prompt of an explainer. It holds no path of this machine, so that it can serve as the explainer's cache key.</summary>
    internal static string Prompt(ExplainerCatalog catalog, ExplainerContext context)
    {
        var narratives = context.Narratives.Count == 0 ? "(none yet)"
            : string.Join("\n", context.Narratives.Select(n => $"- `{n.Key}`{(n.Name is null ? "" : $", {n.Name}")}: {n.Text}"));
        return Prompts.Fill(Prompts.Read("explain.md"),
            ("commits", context.Commits.TrimEnd()),
            ("narratives", narratives),
            ("catalog", Catalog(catalog)));
    }

    /// <summary>Returns the catalog as the prompt lists it, in the groups of <c>docs/explainers.md</c> section 4.1.</summary>
    private static string Catalog(ExplainerCatalog catalog)
    {
        var cs = catalog.Changeset;
        var sb = new StringBuilder();
        sb.Append("### The reading path's first tier\n\nThe review put these first, each with the signal that put it there. The explainer must cover every one.\n\n");
        Steps(sb, catalog, catalog.FirstTier);
        sb.Append("\n### The rest of the reading path\n\n");
        Steps(sb, catalog, catalog.SecondTier);
        sb.Append("\n### Levels and views\n\nA `level` target shows a whole level or view. Name `blocks` from its blocks to bring them out.\n\n");
        List(sb, catalog.Levels);
        sb.Append("\n### Changed edges\n\nAn edge from one block to another counts the references from types in the first to types in the second. Each lists the type pairs whose references changed, base → head.\n\n");
        var hunksOf = cs.Hunks.ToDictionary(h => h.Path, StringComparer.Ordinal);
        if (catalog.Edges.Count == 0) sb.Append("(none)\n");
        foreach (var e in catalog.Edges)
        {
            sb.Append($"- `{e.Id}`: {e.Description}\n");
            foreach (var p in e.Pairs.Take(NarrativeWriter.PairsPerEdge)) sb.Append($"  - pair `{p}`\n");
            if (e.Pairs.Count > NarrativeWriter.PairsPerEdge) sb.Append($"  - and {e.Pairs.Count - NarrativeWriter.PairsPerEdge} more pairs\n");
        }
        sb.Append("\n### Changed blocks\n\nThe explainer must cover every one: point a slide at it, at a level that brings it out, or at something inside it.\n\n");
        List(sb, catalog.Blocks);
        sb.Append("\n### Decisions\n\nA decision is a hunk that adds, removes, renames, moves or changes the signature of a declaration. Its consequences are the hunks that follow from it.\n\n");
        if (catalog.Decisions.Count == 0) sb.Append("(none)\n");
        var decisionAt = cs.Decisions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var d in catalog.Decisions)
        {
            sb.Append($"- `{d.Id}` {d.Description}\n");
            var decision = decisionAt[d.Target.Id];
            if (!hunksOf.TryGetValue(decision.File, out var fh) || decision.Hunk >= fh.Hunks.Count) continue;
            var hunk = fh.Hunks[decision.Hunk];
            var lines = hunk.Lines.Select(l => l[0] + l[1]).Prepend(hunk.Header).ToList();
            sb.Append("  ```diff\n");
            foreach (var line in lines.Take(NarrativeWriter.DiffLinesPerNode)) sb.Append("  ").Append(line).Append('\n');
            if (lines.Count > NarrativeWriter.DiffLinesPerNode) sb.Append($"  … {lines.Count - NarrativeWriter.DiffLinesPerNode} more lines\n");
            sb.Append("  ```\n");
        }
        sb.Append("\n### Changed files and their hunks\n\nA `hunk` target opens the file at that hunk. Hunks that only follow from a decision, or only touch comments, whitespace or layout, are left out.\n\n");
        if (catalog.Files.Count == 0) sb.Append("(none)\n");
        foreach (var f in catalog.Files)
        {
            sb.Append($"- `{f.Path}` ({f.Status})");
            sb.Append(f.Hunks.Count == 0 ? ": no hunks with edits of their own\n" : "\n");
            foreach (var h in f.Hunks) sb.Append($"  - `{h.Id}`: {h.Description}\n");
            if (f.More > 0) sb.Append($"  - and {f.More} more hunks\n");
        }
        sb.Append("\n### Types added or moved\n\n");
        List(sb, catalog.Types);
        sb.Append("\n### Concepts new in the change\n\nA concept is a run of words that the names of several unrelated types share.\n\n");
        List(sb, catalog.Concepts);
        return sb.ToString().TrimEnd();
    }

    private static void List(StringBuilder sb, IReadOnlyList<CatalogEntry> entries)
    {
        if (entries.Count == 0) sb.Append("(none)\n");
        foreach (var e in entries) sb.Append($"- `{e.Id}`: {e.Description}\n");
    }

    /// <summary>Lists steps of the path, each with its signal, the identifier of its target and the files it names.</summary>
    private static void Steps(StringBuilder sb, ExplainerCatalog catalog, IReadOnlyList<PathStep> steps)
    {
        if (steps.Count == 0) sb.Append("(none)\n");
        foreach (var s in steps)
        {
            sb.Append($"- {s.Signal}.");
            if (catalog.StepTargets.TryGetValue(s.Id, out var target)) sb.Append($" Target: `{target}`.");
            var files = s.Files.Where(catalog.CitableFiles.Contains).ToList();
            if (files.Count > 0) sb.Append($" Files: {string.Join(", ", files.Select(f => $"`{f}`"))}.");
            if (s.Members is { Count: > 0 } members && s.Kind == "tests") sb.Append($" Tests: {string.Join("; ", members)}.");
            sb.Append('\n');
        }
    }

    /// <summary>
    /// Returns the section added to the prompt for the one retry: the explainer as answered, with the reason each rejected
    /// slide was rejected, and the steps and blocks no slide covers.
    /// </summary>
    private static string RetrySection(ExplainerCatalog catalog, List<SlideCheck> first, List<ExplainerGap> gaps)
    {
        var sb = new StringBuilder("\n\n## The earlier answer\n\nAn earlier answer gave these slides, numbered.");
        sb.Append(first.Any(c => !c.Passed) ? " Some were rejected, for the reasons given.\n\n" : "\n\n");
        for (var i = 0; i < first.Count; i++)
        {
            var c = first[i];
            sb.Append($"{i + 1}. {c.Title}");
            sb.Append(c.Passed ? $" (target `{c.Entry!.Id}`): {c.Slide!.Text}\n" : $": rejected, because {c.Reason}.\n");
        }
        if (gaps.Count > 0)
        {
            var steps = catalog.FirstTier.ToDictionary(s => s.Id, StringComparer.Ordinal);
            sb.Append("\nNo slide covers these, which the explainer must cover:\n\n");
            foreach (var g in gaps)
            {
                if (g.Kind == "step")
                    sb.Append($"- the first-tier step: {steps[g.Id].Signal}{(catalog.StepTargets.TryGetValue(g.Id, out var t) ? $" (`{t}`)" : "")}\n");
                else sb.Append($"- the changed block `block:{g.Id}`\n");
            }
        }
        sb.Append("""

            ## How to answer again

            Answer only with the slides that change. For each rejected slide, give a corrected slide with `replaces` set to its number, pointing only at what the catalog lists. For each thing no slide covers, either give a new slide about it, with `replaces` set to 0 and `after` set to the number of the slide it follows (0 to put it first), or give an earlier slide again with a sentence that names it, with `replaces` set to that slide's number. A rejected slide that is not given again is dropped.
            """);
        return sb.ToString();
    }

    // ---------- JSON ----------

    private static IEnumerable<JsonElement> Slides(JsonElement answer) =>
        answer.TryGetProperty("slides", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
}
