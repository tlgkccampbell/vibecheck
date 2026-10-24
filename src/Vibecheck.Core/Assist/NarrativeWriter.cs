using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vibecheck.Analysis;
using Vibecheck.Diffing;
using Vibecheck.Model;

namespace Vibecheck.Assist;

/// <summary>
/// Represents a narrative: two or three sentences, written by Claude, that say why one part of a changeset changed,
/// and the evidence it rests on. See <c>docs/review-workflow.md</c> section 6.
/// </summary>
/// <param name="Key">The part explained: <see cref="SummaryKey"/>, an <see cref="EdgeKey"/> or a <see cref="NodeKey"/>.</param>
/// <param name="Text">The narrative.</param>
/// <param name="Cites">The evidence cited: type pairs as <c>From → To</c> for an edge, file paths for a node, and node identifiers for the summary.</param>
public sealed record Narrative(string Key, string Text, IReadOnlyList<string> Cites)
{
    /// <summary>The key of the summary of the whole changeset.</summary>
    public const string SummaryKey = "summary";

    /// <summary>Returns the key of the narrative of an edge between two blocks, such as <c>edge:a.x→b</c>.</summary>
    public static string EdgeKey(string from, string to) => $"edge:{from}→{to}";

    /// <summary>Returns the key of the narrative of a node, such as <c>node:a.x</c>.</summary>
    public static string NodeKey(string id) => "node:" + id;

    /// <summary>Returns how a type pair is cited: <c>From → To</c>.</summary>
    public static string PairCite(string from, string to) => $"{from} → {to}";
}

/// <summary>Holds what every narrative of one changeset is written from.</summary>
/// <param name="Changeset">The changeset explained.</param>
/// <param name="Base">The analysis of the base, whose reference sites show what a changed type pair lost.</param>
/// <param name="Head">The analysis of the head, whose reference sites show what a changed type pair gained.</param>
/// <param name="Commits">The commit messages in the range, as <see cref="NarrativeContext.ReadCommits"/> lists them.</param>
public sealed record NarrativeContext(Changeset Changeset, AnalysisResult Base, AnalysisResult Head, string Commits)
{
    private Dictionary<(string, string), List<Site>>? baseSites, headSites;

    /// <summary>
    /// Gets the directory Claude reads the code at head in, with the paths of the changeset relative to it, or
    /// <see langword="null"/> to give Claude no tools.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the model that writes the narratives, or <see langword="null"/> for the Claude Code default.</summary>
    public string? Model { get; init; }

    /// <summary>The most commits <see cref="ReadCommits"/> lists. A longer range lists its latest commits.</summary>
    public const int MaxCommits = 60;

    /// <summary>
    /// Returns the messages of the commits from a base to a head, oldest first, each as its short hash and subject
    /// with the body indented below. Long bodies are cut, and a range of more than <see cref="MaxCommits"/> commits
    /// lists its latest ones.
    /// </summary>
    public static string ReadCommits(string top, string baseCommit, string headCommit)
    {
        var log = Git.TryRun(top, "log", $"--max-count={MaxCommits}", "--format=%h %s%n%b%x1e", $"{baseCommit}..{headCommit}") ?? "";
        var commits = log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse().ToList();
        var total = Git.TryRun(top, "rev-list", "--count", $"{baseCommit}..{headCommit}");
        var sb = new StringBuilder();
        if (int.TryParse(total, out var n) && n > commits.Count) sb.Append($"The range has {n} commits; the latest {commits.Count} are listed.\n");
        foreach (var c in commits)
        {
            var lines = c.Split('\n');
            sb.Append("- ").Append(lines[0]).Append('\n');
            var body = string.Join("\n", lines.Skip(1)).Trim();
            if (body.Length > 600) body = body[..600] + " …";
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries)) sb.Append("  ").Append(line.TrimEnd()).Append('\n');
        }
        return sb.Length == 0 ? "(no commit messages)\n" : sb.ToString();
    }

    /// <summary>A reference from one type to another, or to an external assembly, as the analysis located it.</summary>
    internal sealed record Site(string Kind, string Symbol, string File, int Line);

    /// <summary>Returns the reference sites of a type pair at base and at head.</summary>
    internal (List<Site> Base, List<Site> Head) Sites(string from, string to)
    {
        baseSites ??= Index(Base);
        headSites ??= Index(Head);
        return (baseSites.GetValueOrDefault((from, to)) ?? [], headSites.GetValueOrDefault((from, to)) ?? []);
    }

    private static Dictionary<(string, string), List<Site>> Index(AnalysisResult analysis)
    {
        var index = new Dictionary<(string, string), List<Site>>();
        void Add(string from, string to, Site site)
        {
            if (!index.TryGetValue((from, to), out var list)) index[(from, to)] = list = [];
            list.Add(site);
        }
        foreach (var d in analysis.Dependencies) Add(d.FromType, d.ToType, new Site(d.Kind, d.Symbol, d.File, d.Line));
        foreach (var e in analysis.ExternalReferences) Add(e.FromType, e.Assembly, new Site("references", e.Symbol, e.File, e.Line));
        return index;
    }
}

/// <summary>Stores checked narratives by the request that produced them. <c>ReviewCache</c> is the real one.</summary>
public interface INarrativeCache
{
    /// <summary>Returns the narratives stored under a request key, or <see langword="null"/> when there are none.</summary>
    IReadOnlyList<Narrative>? ReadNarratives(string key);

    /// <summary>Stores the narratives a request produced under its key.</summary>
    void WriteNarratives(string key, IReadOnlyList<Narrative> narratives);
}

/// <summary>Reports how many narratives a run of a plan wrote, how many failed their check twice, and which batches failed.</summary>
public sealed record NarrativeTally(int Written, int Dropped, IReadOnlyList<string> Failures);

/// <summary>
/// Writes the narratives of a changeset with Claude, one batch at a time, and checks each against the evidence before
/// it is accepted. An edge's narrative must cite only type pairs behind the edge, a node's only files under the node,
/// and the summary only nodes of the map. Narratives that fail, or that the answer leaves out, are requested once more
/// on their own, and dropped if they fail again. See <c>docs/review-workflow.md</c> sections 6.2 and 6.3.
/// </summary>
public static class NarrativeWriter
{
    /// <summary>The most requests to Claude that run at once.</summary>
    public const int MaxParallel = 4;

    /// <summary>The tools Claude may use to look past the inputs, in the head worktree.</summary>
    public static readonly IReadOnlyList<string> Tools = ["Read", "Grep", "Glob"];

    /// <summary>How long one request may take. A request that takes longer counts as failed, so that one hung call cannot keep the narratives from finishing.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private const int PairsPerEdge = 8, SitesPerSide = 4, DiffLinesPerNode = 160, FilesPerNode = 30, SummaryEdges = 12;

    /// <summary>
    /// Writes every batch of a plan, starting them in the plan's order with at most <see cref="MaxParallel"/> running
    /// at once, and returns the tally. Each narrative is passed to <paramref name="accepted"/> as soon as it passes
    /// its check. A batch that fails is reported in the tally and the others go on.
    /// </summary>
    /// <param name="regenerate">A value indicating whether stored narratives are ignored and written anew.</param>
    public static async Task<NarrativeTally> RunAsync(IReadOnlyList<NarrativeBatch> plan, NarrativeContext context, IClaude claude, INarrativeCache? cache,
        Action<Narrative> accepted, bool regenerate = false, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(MaxParallel);
        var failures = new List<string>();
        var (written, dropped) = (0, 0);
        var tasks = new List<Task>();
        foreach (var batch in plan)
        {
            await gate.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var narratives = await WriteAsync(batch, context, claude, cache, accepted, regenerate, ct);
                    lock (failures)
                    {
                        written += narratives.Count;
                        dropped += batch.Keys.Count() - narratives.Count;
                    }
                }
                catch (ClaudeException ex)
                {
                    lock (failures)
                    {
                        failures.Add($"{batch.Id}: {ex.Message}");
                        dropped += batch.Keys.Count();
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }
        await Task.WhenAll(tasks);
        return new NarrativeTally(written, dropped, failures);
    }

    /// <summary>
    /// Returns the checked narratives of one batch, from the cache when it holds the same request and
    /// <paramref name="regenerate"/> is false, and otherwise from Claude, storing them. Each is passed to
    /// <paramref name="accepted"/> as it passes its check.
    /// </summary>
    /// <exception cref="ClaudeException">Claude failed on the batch's first request.</exception>
    public static async Task<IReadOnlyList<Narrative>> WriteAsync(NarrativeBatch batch, NarrativeContext context, IClaude claude, INarrativeCache? cache,
        Action<Narrative> accepted, bool regenerate = false, CancellationToken ct = default)
    {
        var key = RequestKey(batch, context);
        if (!regenerate && cache?.ReadNarratives(key) is { } stored)
        {
            foreach (var n in stored) accepted(n);
            return stored;
        }
        var narratives = await AskCheckedAsync(batch, context, claude, accepted, ct);
        cache?.WriteNarratives(key, narratives);
        return narratives;
    }

    /// <summary>
    /// Writes one narrative of a batch anew, on its own, and returns it, or <see langword="null"/> when it fails its
    /// check twice. The batch's stored narratives are updated with it.
    /// </summary>
    /// <exception cref="ClaudeException">Claude failed.</exception>
    public static async Task<Narrative?> RegenerateAsync(NarrativeBatch batch, string key, NarrativeContext context, IClaude claude, INarrativeCache? cache, CancellationToken ct = default)
    {
        var one = Only(batch, [key]);
        var narrative = (await AskCheckedAsync(one, context, claude, _ => { }, ct)).SingleOrDefault();
        if (narrative is not null && cache is not null)
        {
            var batchKey = RequestKey(batch, context);
            var stored = cache.ReadNarratives(batchKey) ?? [];
            cache.WriteNarratives(batchKey, [.. stored.Where(n => n.Key != key), narrative]);
        }
        return narrative;
    }

    /// <summary>
    /// Returns the key a batch's narratives are stored under: a hash of everything that decides the answer, which is
    /// the prompt, the schema and the model. The prompt holds the prompt template, so a new version of it is a new key.
    /// </summary>
    internal static string RequestKey(NarrativeBatch batch, NarrativeContext context) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', Prompt(batch, context), Schema(batch), context.Model ?? ""))))[..24];

    private static string Schema(NarrativeBatch batch) => Prompts.Read(batch.Kind == "summary" ? "summary.schema.json" : "narratives.schema.json");

    /// <summary>Asks for a batch, checks the answer, and asks once more, on their own, for the narratives that failed or were left out.</summary>
    private static async Task<List<Narrative>> AskCheckedAsync(NarrativeBatch batch, NarrativeContext context, IClaude claude, Action<Narrative> accepted, CancellationToken ct)
    {
        var (good, rejected) = Check(batch, context.Changeset, await AskAsync(batch, context, claude, null, ct));
        foreach (var n in good) accepted(n);
        if (rejected.Count == 0) return good;
        try
        {
            var retry = Only(batch, rejected.Keys);
            var (again, _) = Check(retry, context.Changeset, await AskAsync(retry, context, claude, rejected, ct));
            foreach (var n in again) accepted(n);
            good.AddRange(again);
        }
        catch (ClaudeException)
        {
            // The narratives accepted from the first answer stand, and the rest are dropped.
        }
        return good;
    }

    private static Task<JsonElement> AskAsync(NarrativeBatch batch, NarrativeContext context, IClaude claude, IReadOnlyDictionary<string, string>? rejected, CancellationToken ct)
    {
        var prompt = Prompt(batch, context);
        var schema = Schema(batch);
        if (rejected is not null)
        {
            prompt += "\n## An earlier answer was rejected\n\nAn earlier answer about these parts was rejected for these reasons. Answer again, and cite only the evidence listed above.\n\n"
                + string.Join("", rejected.Select(r => $"- {r.Key}: {r.Value}\n"));
        }
        var request = new ClaudeRequest(prompt, schema)
        {
            Model = context.Model,
            Timeout = RequestTimeout,
            WorkingDirectory = context.WorkingDirectory,
            Tools = context.WorkingDirectory is null ? [] : Tools,
        };
        return claude.AskAsync(request, ct);
    }

    /// <summary>Returns a batch narrowed to the narratives with the keys given.</summary>
    private static NarrativeBatch Only(NarrativeBatch batch, IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        return batch with
        {
            Edges = batch.Edges.Where(e => set.Contains(e.Key)).ToList(),
            Nodes = batch.Nodes.Where(n => set.Contains(Narrative.NodeKey(n))).ToList(),
        };
    }

    /// <summary>
    /// Checks an answer against a batch. Returns the narratives that pass, and for each requested narrative that fails
    /// or is missing, the reason. A narrative of something the batch did not ask about is ignored.
    /// </summary>
    internal static (List<Narrative> Good, Dictionary<string, string> Rejected) Check(NarrativeBatch batch, Changeset changeset, JsonElement answer)
    {
        var good = new List<Narrative>();
        var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        if (batch.Kind == "summary")
        {
            var text = Text(answer);
            var parts = Strings(answer, "parts");
            var unknown = parts.Where(p => !changeset.Map.Nodes.ContainsKey(p) && p != ArchitectureMap.Unsorted).ToList();
            if (text is null) rejected[Narrative.SummaryKey] = "the summary is empty";
            else if (parts.Count == 0) rejected[Narrative.SummaryKey] = "the summary cites no part of the map";
            else if (unknown.Count > 0) rejected[Narrative.SummaryKey] = $"the summary cites {string.Join(", ", unknown)}, which the map does not have";
            else good.Add(new Narrative(Narrative.SummaryKey, text, parts));
            return (good, rejected);
        }

        var edges = batch.Edges.ToDictionary(e => e.Key, StringComparer.Ordinal);
        foreach (var e in Array(answer, "edges"))
        {
            var key = Narrative.EdgeKey(Str(e, "from"), Str(e, "to"));
            if (!edges.TryGetValue(key, out var edge) || good.Any(n => n.Key == key)) continue;
            var evidence = edge.Evidence.Select(p => Narrative.PairCite(p.From, p.To)).ToHashSet(StringComparer.Ordinal);
            var pairs = Array(e, "pairs").Select(p => Narrative.PairCite(Str(p, "from"), Str(p, "to"))).Distinct(StringComparer.Ordinal).ToList();
            var foreign = pairs.Where(p => !evidence.Contains(p)).ToList();
            var text = Text(e);
            if (text is null) rejected[key] = "the narrative is empty";
            else if (pairs.Count == 0 && evidence.Count > 0) rejected[key] = "the narrative cites no type pair";
            else if (foreign.Count > 0) rejected[key] = $"the narrative cites {string.Join(", ", foreign)}, which is not among the edge's changed type pairs";
            else { good.Add(new Narrative(key, text, pairs)); rejected.Remove(key); }
        }
        foreach (var n in Array(answer, "nodes"))
        {
            var id = Str(n, "id");
            var key = Narrative.NodeKey(id);
            if (!batch.Nodes.Contains(id) || good.Any(g => g.Key == key)) continue;
            var under = FilesUnder(changeset, id).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var files = Strings(n, "files").Distinct(StringComparer.Ordinal).ToList();
            var foreign = files.Where(f => !under.Contains(f)).ToList();
            var text = Text(n);
            if (text is null) rejected[key] = "the narrative is empty";
            else if (files.Count == 0) rejected[key] = "the narrative cites no file";
            else if (foreign.Count > 0) rejected[key] = $"the narrative cites {string.Join(", ", foreign)}, which is not under the node";
            else { good.Add(new Narrative(key, text, files)); rejected.Remove(key); }
        }
        foreach (var key in batch.Keys)
            if (!rejected.ContainsKey(key) && good.All(n => n.Key != key)) rejected[key] = "the answer gave no narrative for it";
        return (good, rejected);
    }

    private static string? Text(JsonElement e) => Str(e, "text").Trim() is { Length: > 0 } t ? t : null;

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static List<string> Strings(JsonElement e, string name) =>
        Array(e, name).Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();

    /// <summary>Returns the files whose types bind under a node at base or at head.</summary>
    private static IEnumerable<FileChange> FilesUnder(Changeset changeset, string id) =>
        changeset.Files.Where(f => f.LeavesHead.Concat(f.LeavesBase).Any(l => Identifiers.IsWithin(l, id)));

    /// <summary>Returns how many changed files under a node have only trivial edits, as <see cref="TrivialEdits"/> defines them.</summary>
    private static int TrivialUnder(Changeset changeset, string id)
    {
        var trivial = changeset.Hunks.Where(h => h.Trivial == true).Select(h => h.Path).ToHashSet(StringComparer.Ordinal);
        return FilesUnder(changeset, id).Count(f => f.Status != "unchanged" && trivial.Contains(f.Path));
    }

    // ---------- Prompts ----------

    /// <summary>Returns the prompt of a batch. It holds no path of this machine, so that it can serve as the batch's cache key.</summary>
    internal static string Prompt(NarrativeBatch batch, NarrativeContext context)
    {
        var cs = context.Changeset;
        if (batch.Kind == "summary") return SummaryPrompt(context);
        var involved = batch.Edges.SelectMany(e => new[] { e.From, e.To }).Concat(batch.Nodes).Distinct(StringComparer.Ordinal).ToList();
        var location = batch.Kind == "view" ? $"the view `{batch.Location}`, {cs.Map.Views[batch.Location].Name}, which regroups the leaves under `{cs.Map.Views[batch.Location].Level}`"
            : batch.Location == Identifiers.Root ? "the top level of the map" : $"the inside of `{batch.Location}`, {NameOf(cs.Map, batch.Location)}";
        return Prompts.Fill(Prompts.Read("narrate.md"),
            ("location", location),
            ("commits", context.Commits.TrimEnd()),
            ("parts", string.Join("\n", involved.Select(id => "- " + Describe(cs.Map, id)))),
            ("edges", batch.Edges.Count == 0 ? "(none)" : string.Join("\n", batch.Edges.Select(e => EdgeSection(e, cs.Map, context)))),
            ("nodes", batch.Nodes.Count == 0 ? "(none)" : string.Join("\n", batch.Nodes.Select(n => NodeSection(n, cs)))));
    }

    private static string SummaryPrompt(NarrativeContext context)
    {
        var cs = context.Changeset;
        var changedFiles = cs.Files.Where(f => f.Status != "unchanged").ToList();
        var sb = new StringBuilder();
        sb.Append($"{changedFiles.Count} files changed: {changedFiles.Count(f => f.Status == "added")} added, {changedFiles.Count(f => f.Status == "removed")} removed, {changedFiles.Count(f => f.Status == "modified")} modified, {changedFiles.Count(f => f.Status == "renamed")} renamed. ");
        sb.Append($"{cs.Types.Count(t => t.Status == "added")} types were added, {cs.Types.Count(t => t.Status == "removed")} removed and {cs.Types.Count(t => t.Status == "moved")} moved to another part.\n\n");
        sb.Append("The parts of the map, with the files that changed under each:\n");
        var tops = cs.Map.Nodes.Keys.Where(id => Identifiers.ParentOf(id) is null).Append(ArchitectureMap.Unsorted).Where(cs.Content.ContainsKey);
        foreach (var id in tops)
        {
            var c = cs.Content[id];
            var changed = c.Added + c.Removed + c.Modified;
            var trivial = TrivialUnder(cs, id);
            sb.Append($"- {Describe(cs.Map, id)} {(changed == 0 ? "Unchanged." : $"{c.Added} added, {c.Removed} removed, {c.Modified} modified{(trivial > 0 ? $" ({trivial} with only trivial edits)" : "")}.")}\n");
        }
        var home = cs.Map.Views.FirstOrDefault(v => v.Value.Level == Identifiers.Root);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        if (home.Key is not null)
        {
            foreach (var (groupId, group) in home.Value.Groups)
                foreach (var leaf in group.Leaves) owners[leaf] = NarrativePlanner.GroupKey(home.Key, groupId);
        }
        else
        {
            foreach (var leaf in cs.Edges.SelectMany(e => new[] { e.From, e.To }))
                if (!cs.Map.Externals.ContainsKey(leaf)) owners[leaf] = leaf == ArchitectureMap.Unsorted ? leaf : Identifiers.TopLevelOf(leaf);
        }
        var edges = NarrativePlanner.Edges(cs, owners, externals: true);
        sb.Append($"\nThe changed dependencies between {(home.Key is null ? "the parts" : $"the groups of the view `{home.Key}`, {home.Value.Name},")} largest change first:\n");
        foreach (var e in edges.Take(SummaryEdges))
        {
            sb.Append($"- {NameOf(cs.Map, e.From)} → {NameOf(cs.Map, e.To)}: {e.Status}, {e.Base} → {e.Head} references");
            var pairs = e.Evidence.Take(3).Select(p => $"`{TypeNames.Plain(p.From)}` → `{TypeNames.Plain(p.To)}`").ToList();
            sb.Append(pairs.Count > 0 ? $", such as {string.Join(", ", pairs)}\n" : "\n");
        }
        if (edges.Count > SummaryEdges) sb.Append($"- and {edges.Count - SummaryEdges} more\n");
        if (edges.Count == 0) sb.Append("- none\n");
        return Prompts.Fill(Prompts.Read("summarize.md"), ("commits", context.Commits.TrimEnd()), ("changes", sb.ToString().TrimEnd()));
    }

    private static string EdgeSection(DrawnEdge e, ArchitectureMap map, NarrativeContext context)
    {
        var sb = new StringBuilder();
        sb.Append($"### `{e.From}` → `{e.To}`\n\n{NameOf(map, e.From)} uses {NameOf(map, e.To)}: {e.Status}, {e.Base} → {e.Head} references.\n");
        if (e.Evidence.Count > 0) sb.Append("The type pairs whose references changed, largest change first:\n");
        foreach (var p in e.Evidence.Take(PairsPerEdge))
        {
            sb.Append($"- `{p.From}` → `{p.To}`: {p.Base} → {p.Head}\n");
            var (before, after) = context.Sites(p.From, p.To);
            var (gained, lost) = (Unmatched(after, before), Unmatched(before, after));
            foreach (var s in gained.Take(SitesPerSide)) sb.Append($"  - added: {s.Kind} `{s.Symbol}` at {s.File}:{s.Line}\n");
            if (gained.Count > SitesPerSide) sb.Append($"  - and {gained.Count - SitesPerSide} more added\n");
            foreach (var s in lost.Take(SitesPerSide)) sb.Append($"  - removed: {s.Kind} `{s.Symbol}` at {s.File}:{s.Line} at base\n");
            if (lost.Count > SitesPerSide) sb.Append($"  - and {lost.Count - SitesPerSide} more removed\n");
        }
        if (e.Evidence.Count > PairsPerEdge) sb.Append($"- and {e.Evidence.Count - PairsPerEdge} more type pairs\n");
        return sb.ToString();
    }

    /// <summary>Returns the sites of one side that the other side does not match by kind, symbol and file. Lines are ignored, since edits elsewhere in a file move them.</summary>
    private static List<NarrativeContext.Site> Unmatched(List<NarrativeContext.Site> side, List<NarrativeContext.Site> other)
    {
        var left = other.GroupBy(s => (s.Kind, s.Symbol, s.File)).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<NarrativeContext.Site>();
        foreach (var s in side)
        {
            var k = (s.Kind, s.Symbol, s.File);
            if (left.TryGetValue(k, out var n) && n > 0) left[k] = n - 1;
            else result.Add(s);
        }
        return result;
    }

    private static string NodeSection(string id, Changeset cs)
    {
        var c = cs.Content[id];
        var sb = new StringBuilder();
        sb.Append($"### `{id}`, {NameOf(cs.Map, id)}\n\n{c.Added} files added, {c.Removed} removed and {c.Modified} modified");
        var trivial = TrivialUnder(cs, id);
        sb.Append(trivial > 0 ? $", {trivial} of them with only trivial edits to comments, whitespace or layout.\n" : ".\n");
        var hunks = cs.Hunks.ToDictionary(h => h.Path, StringComparer.Ordinal);
        var changed = FilesUnder(cs, id).Where(f => f.Status != "unchanged")
            .OrderBy(f => hunks.TryGetValue(f.Path, out var h) && h.Trivial == true ? 1 : 0).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();
        var budget = DiffLinesPerNode;
        foreach (var f in changed.Take(FilesPerNode))
        {
            sb.Append($"- {f.Path} ({f.Status})\n");
            if (budget <= 0 || !hunks.TryGetValue(f.Path, out var fh) || fh.Trivial == true) continue;
            var lines = fh.Hunks.Where(h => h.Trivial != true).SelectMany(h => h.Lines.Select(l => l[0] + l[1]).Prepend(h.Header)).Take(budget).ToList();
            if (lines.Count == 0) continue;
            budget -= lines.Count;
            sb.Append("```diff\n").Append(string.Join("\n", lines)).Append("\n```\n");
        }
        if (changed.Count > FilesPerNode) sb.Append($"- and {changed.Count - FilesPerNode} more files\n");
        return sb.ToString();
    }

    /// <summary>Returns the name of a node, group or external, or the identifier itself when the map has no name for it.</summary>
    private static string NameOf(ArchitectureMap map, string id)
    {
        if (map.Nodes.TryGetValue(id, out var n)) return n.Name;
        if (map.Externals.TryGetValue(id, out var x)) return x.Name;
        if (TryGroup(map, id, out _, out var g)) return g.Name;
        return id == ArchitectureMap.Unsorted ? "Unsorted" : id;
    }

    /// <summary>Describes a node, group or external for a prompt: its identifier, its name and its purpose.</summary>
    private static string Describe(ArchitectureMap map, string id)
    {
        if (map.Nodes.TryGetValue(id, out var n)) return $"`{id}`, {n.Name}: {n.Purpose ?? "(no purpose given)"}";
        if (map.Externals.TryGetValue(id, out var x)) return $"`{id}`, {x.Name}, an external library: {x.Purpose ?? "(no purpose given)"}";
        if (TryGroup(map, id, out var view, out var g)) return $"`{id}`, {g.Name}, a group of the view {view}: {g.Purpose ?? "(no purpose given)"} It holds {string.Join(", ", g.Leaves.Select(l => $"`{l}`"))}.";
        return id == ArchitectureMap.Unsorted ? "`unsorted`: the types no leaf of the map binds." : $"`{id}`";
    }

    private static bool TryGroup(ArchitectureMap map, string key, out string view, out GroupSpec group)
    {
        (view, group) = ("", null!);
        if (!key.StartsWith("g:", StringComparison.Ordinal)) return false;
        var slash = key.IndexOf('/');
        if (slash < 0 || !map.Views.TryGetValue(key[2..slash], out var v) || !v.Groups.TryGetValue(key[(slash + 1)..], out var g)) return false;
        (view, group) = (v.Name, g);
        return true;
    }
}
