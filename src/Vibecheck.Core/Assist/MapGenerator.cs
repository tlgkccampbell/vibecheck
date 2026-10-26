using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vibecheck.Analysis;
using Vibecheck.Bootstrap;
using Vibecheck.Checking;
using Vibecheck.Format;
using Vibecheck.Model;

namespace Vibecheck.Assist;

/// <summary>Holds a map that Claude proposed and the checker verified: the map, its canonical text, the round it came from, and what the checker still reports about it.</summary>
public sealed record GeneratedMap(ArchitectureMap Map, string Text, int Round, IReadOnlyList<string> Findings);

/// <summary>Holds one thing the checker reports about a map, and the full names of the types it concerns.</summary>
internal sealed record Finding(string Text, IReadOnlyList<string> Types);

/// <summary>
/// Runs the bootstrapping loop of <c>docs/vibecheck-spec.md</c> section 5.3 without a person in it. Claude proposes a
/// concept map from a listing of the solution, the checker reports cycles, unsorted types, ambiguous bindings and
/// oversized leaves, and Claude edits the map until the checker finds nothing or the rounds run out. See
/// <c>docs/review-workflow.md</c> section 4.4.
/// </summary>
public static class MapGenerator
{
    /// <summary>The most rounds of proposing and revising that one generation runs.</summary>
    public const int MaxRounds = 3;

    /// <summary>A leaf with more types than this is reported as too large for a view to regroup.</summary>
    public const int LargeLeaf = 60;

    /// <summary>
    /// The longest listing, in characters, that lists a solution by unit. A larger solution is listed by namespace,
    /// which keeps both the prompt and the map Claude writes from growing with the number of types.
    /// </summary>
    public const int MaxListing = 120_000;

    /// <summary>Gets how long one proposal or revision may take before it is abandoned as failed.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Line = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Proposes a concept map for an analysis and revises it against the checker's findings. The skeleton supplies the
    /// solution, the exclusions, the test types and the externals, which the generated map keeps. Returns the usable proposal with the
    /// fewest findings, preferring the later of two that tie. Each revision edits that proposal. A revision that fails, or takes longer than
    /// <see cref="RequestTimeout"/>, ends the rounds and keeps the best proposal so far. While Claude works, the progress
    /// says every few seconds how long the request has run and how much Claude has thought and written. The log
    /// receives each reason a proposal could not be used as a map.
    /// </summary>
    /// <exception cref="ClaudeException">The proposal failed, or no proposal could be used as a map.</exception>
    public static Task<GeneratedMap> GenerateAsync(ArchitectureMap skeleton, AnalysisResult analysis, IClaude claude, IProgress<string>? progress = null, CancellationToken ct = default, string? model = null, Action<string>? log = null) =>
        GenerateAsync(skeleton, analysis, claude, progress, ct, model, log, MaxListing);

    /// <summary>
    /// Proposes and revises a concept map as the public overload does, listing the solution by namespace when its
    /// unit listing is longer than <paramref name="maxListing"/> characters.
    /// </summary>
    /// <exception cref="ClaudeException">The proposal failed, or no proposal could be used as a map.</exception>
    internal static async Task<GeneratedMap> GenerateAsync(ArchitectureMap skeleton, AnalysisResult analysis, IClaude claude, IProgress<string>? progress, CancellationToken ct, string? model, Action<string>? log, int maxListing)
    {
        var errors = new List<string>();
        var (included, _) = Exclusion.Parse(skeleton, errors).Apply(analysis);
        var units = Units.Build(included);
        var (byNamespace, format, listing) = Listing(included, units, maxListing);
        var rules = Prompts.Fill(Prompts.Read("map-rules.md"),
            ("externals", skeleton.Externals.Count == 0 ? "none" : string.Join(", ", skeleton.Externals.Keys.Select(k => $"`{k}`"))),
            ("exclude", string.Join(", ", skeleton.Exclude.Concat(skeleton.Tests).Select(e => $"`{e}`"))));
        var (mapSchema, editSchema) = (Prompts.Read("map.schema.json"), Prompts.Read("map-edits.schema.json"));
        string Propose(IReadOnlyList<string> rejected) => Prompts.Fill(Prompts.Read("propose-map.md"),
            ("rules", rules), ("format", format), ("rejected", Rejected(rejected)), ("listing", listing));

        progress?.Report($"proposing a concept map from {included.Types.Count} types{(byNamespace ? ", listed by namespace" : "")}");
        ClaudeRequest Request(string prompt, string schema, string step) => new(prompt, schema)
        {
            Model = model, Timeout = RequestTimeout, Activity = progress is null ? null : a => progress.Report($"{step}: {a.Describe()}"),
        };
        var answer = await claude.AskAsync(Request(Propose([]), mapSchema, "proposing"), ct);
        var problems = new List<string>();
        var candidate = Build(skeleton, answer, problems);
        GeneratedMap? best = null;
        IReadOnlyList<Finding> bestFindings = [];
        IReadOnlyList<string> findings = [];
        var previous = int.MaxValue;
        for (var round = 1; ; round++)
        {
            var (map, text, found) = Check(candidate, problems, analysis, largeLeaves: !byNamespace);
            findings = found.Select(f => f.Text).ToList();
            if (map is null)
            {
                progress?.Report($"round {round}: the map cannot be used, for {found.Count} reason(s)");
                foreach (var f in findings) log?.Invoke($"round {round} was rejected: {f}");
            }
            else progress?.Report(found.Count == 0 ? $"round {round}: the checker found nothing to fix" : $"round {round}: the checker reports {found.Count} finding(s)");
            if (map is not null && (best is null || found.Count <= best.Findings.Count))
                (best, bestFindings) = (new GeneratedMap(map, text, round, findings), found);
            // A usable revision that fixes nothing usually meets a cycle in the code itself, which no grouping removes,
            // so another round would only spend the reviewer's usage. An unusable revision gets another round.
            var stalled = map is not null && found.Count >= previous;
            if (found.Count == 0 || round == MaxRounds || stalled) break;
            previous = map is null ? previous : found.Count;
            var rejected = map is null ? findings : [];

            // Until some proposal is usable there is no map to edit, so Claude proposes a whole map again.
            progress?.Report($"{(best is null ? "proposing" : "revising")} the concept map (round {round + 1})");
            string prompt;
            if (best is null) prompt = Propose(rejected);
            else
            {
                var named = bestFindings.SelectMany(f => f.Types).ToHashSet(StringComparer.Ordinal);
                var (_, context, types) = named.Count == 0 ? (false, "", "The findings name no types.") : Listing(included, units, maxListing, named);
                prompt = Prompts.Fill(Prompts.Read("revise-map.md"), ("rules", rules), ("map", Describe(best.Map)),
                    ("findings", string.Join("\n\n", bestFindings.Select(f => "- " + f.Text.Replace("\n", "\n  ")))),
                    ("rejected", Rejected(rejected)), ("format", context), ("listing", types));
            }
            var editing = best;
            try
            {
                answer = await claude.AskAsync(Request(prompt, editing is null ? mapSchema : editSchema, $"round {round + 1}"), ct);
            }
            catch (ClaudeException ex) when (best is not null)
            {
                progress?.Report($"round {round + 1} failed, so the map from round {best.Round} is kept: {ex.Message}");
                break;
            }
            problems = [];
            candidate = editing is null ? Build(skeleton, answer, problems) : MapEdits.Apply(editing.Map, answer, problems);
        }
        return best ?? throw new ClaudeException("Claude proposed no usable map: " + string.Join("; ", findings.Take(5)));
    }

    /// <summary>
    /// Returns the listing of a solution that a prompt carries: by unit when that fits in <paramref name="maxListing"/>
    /// characters, and otherwise by namespace, with as much detail per namespace as fits. Also returns the text that
    /// explains the listing's format, and whether it lists by namespace.
    /// </summary>
    /// <param name="included">The analysis with the map's exclusions applied.</param>
    /// <param name="units">The units of that analysis.</param>
    /// <param name="maxListing">The longest listing by unit, in characters.</param>
    /// <param name="only">The types to list, with their units or namespaces, or <see langword="null"/> to list the whole solution.</param>
    internal static (bool ByNamespace, string Format, string Listing) Listing(AnalysisResult included, List<Unit> units, int maxListing, IReadOnlySet<string>? only = null)
    {
        var listed = only is null ? units : units.Where(u => u.Members.Any(only.Contains)).ToList();
        var text = Units.Listing(listed, listed.Sum(u => u.Members.Count));
        if (text.Length <= maxListing) return (false, Prompts.Read("listing-units.md").TrimEnd(), text);
        foreach (var (names, summaries) in new[] { (12, 2), (6, 1), (0, 0) })
        {
            text = Units.NamespaceListing(included, units, names, summaries, only);
            if (text.Length <= maxListing) break;
        }
        return (true, Prompts.Read("listing-namespaces.md").TrimEnd(), text);
    }

    /// <summary>Returns the section of a prompt that says why the last answer could not be used, or an empty string when it could.</summary>
    private static string Rejected(IReadOnlyList<string> reasons) => reasons.Count == 0 ? ""
        : Prompts.Fill(Prompts.Read("rejected-map.md"), ("reasons", string.Join("\n", reasons.Select(r => "- " + r.Replace("\n", "\n  ")))));

    /// <summary>Returns a map's nodes and views in the shape a proposal takes, one JSON object to a line.</summary>
    internal static string Describe(ArchitectureMap map)
    {
        var lines = map.Nodes.Select(kv => JsonSerializer.Serialize(new { id = kv.Key, name = kv.Value.Name, purpose = kv.Value.Purpose, category = kv.Value.Category, bind = kv.Value.Bind }, Line))
            .Concat(map.Views.Select(kv => JsonSerializer.Serialize(new
            {
                view = kv.Key, name = kv.Value.Name, purpose = kv.Value.Purpose, level = kv.Value.Level,
                groups = kv.Value.Groups.Select(g => new { id = g.Key, name = g.Value.Name, purpose = g.Value.Purpose, leaves = g.Value.Leaves }),
            }, Line)));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds the map an answer describes and checks it. Returns a null map when it cannot be used at all, with the
    /// reasons as findings, and otherwise the map, its canonical text and what the checker reports about it.
    /// </summary>
    internal static (ArchitectureMap? Map, string Text, IReadOnlyList<string> Findings) Evaluate(ArchitectureMap skeleton, JsonElement answer, AnalysisResult analysis)
    {
        var problems = new List<string>();
        var (map, text, findings) = Check(Build(skeleton, answer, problems), problems, analysis, largeLeaves: true);
        return (map, text, findings.Select(f => f.Text).ToList());
    }

    /// <summary>Returns the map a proposal describes, with the skeleton's solution, exclusions, test types and externals, and adds what is wrong with its nodes to the problems.</summary>
    private static ArchitectureMap Build(ArchitectureMap skeleton, JsonElement answer, List<string> problems)
    {
        var map = new ArchitectureMap { Version = skeleton.Version, Solution = skeleton.Solution, Exclude = [.. skeleton.Exclude], Tests = [.. skeleton.Tests] };
        foreach (var (k, v) in skeleton.Externals) map.Externals[k] = v;
        foreach (var node in answer.GetProperty("nodes").EnumerateArray())
        {
            var id = node.GetProperty("id").GetString()!;
            if (map.Nodes.ContainsKey(id)) problems.Add($"node \"{id}\" is listed twice");
            var bind = node.TryGetProperty("bind", out var b) ? b.EnumerateArray().Select(p => p.GetString()!).ToList() : null;
            map.Nodes[id] = new NodeSpec
            {
                Name = node.GetProperty("name").GetString()!,
                Purpose = Text(node, "purpose"),
                Category = Text(node, "category"),
                Bind = bind is { Count: > 0 } ? bind : null,
            };
        }
        foreach (var view in answer.GetProperty("views").EnumerateArray())
        {
            var groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal);
            foreach (var g in view.GetProperty("groups").EnumerateArray())
            {
                groups[g.GetProperty("id").GetString()!] = new GroupSpec
                {
                    Name = g.GetProperty("name").GetString()!,
                    Purpose = Text(g, "purpose"),
                    Leaves = g.GetProperty("leaves").EnumerateArray().Select(l => l.GetString()!).ToList(),
                };
            }
            map.Views[view.GetProperty("id").GetString()!] = new ViewSpec
            {
                Name = view.GetProperty("name").GetString()!, Purpose = Text(view, "purpose"), Level = view.GetProperty("level").GetString()!, Groups = groups,
            };
        }
        return map;
    }

    /// <summary>
    /// Checks a map. Returns a null map when it cannot be used at all, with the problems and the reasons as findings,
    /// and otherwise the map as the reader reads it back, its canonical text, and the problems and what the checker
    /// reports about it.
    /// </summary>
    /// <param name="largeLeaves">A value indicating whether leaves too large for a view to regroup are reported.</param>
    private static (ArchitectureMap? Map, string Text, IReadOnlyList<Finding> Findings) Check(ArchitectureMap map, List<string> problems, AnalysisResult analysis, bool largeLeaves)
    {
        var text = MapWriter.Write(map);
        var errors = new List<string>();
        var parsed = MapReader.Read(text, errors);
        IEnumerable<Finding> Plain(IEnumerable<string> texts) => texts.Select(t => new Finding(t, []));
        if (parsed is null || errors.Count > 0) return (null, text, [.. Plain(problems), .. Plain(errors)]);
        var facts = Checker.Run(parsed, analysis, new CheckOptions { MapText = text });
        if (!facts.Passed) return (null, text, [.. Plain(problems), .. Plain(facts.Violations.Select(v => v.Message))]);
        return (parsed, text, [.. Plain(problems), .. Findings(facts, largeLeaves)]);
    }

    /// <summary>
    /// Returns what the checker's facts say is wrong with a usable map: unsorted types, types two leaves claim equally,
    /// cycles among a level's children with the type pairs behind each edge, cycles among a view's groups, and, when
    /// asked for, leaves too large for a view to regroup. Each finding names the types it concerns.
    /// </summary>
    /// <param name="facts">The checker's facts about the map.</param>
    /// <param name="largeLeaves">A value indicating whether leaves too large for a view to regroup are reported.</param>
    internal static List<Finding> Findings(Facts facts, bool largeLeaves = true)
    {
        var findings = new List<Finding>();
        var unsorted = facts.Types.Where(t => t.Leaf == ArchitectureMap.Unsorted).Select(t => t.Name).ToList();
        if (unsorted.Count > 0)
            findings.Add(new($"{unsorted.Count} type(s) are unsorted, because no leaf binds them: {string.Join(", ", unsorted.Take(60))}{(unsorted.Count > 60 ? ", …" : "")}", unsorted));
        const string claimed = " is claimed equally";
        foreach (var w in facts.Warnings.Where(w => w.Contains(claimed, StringComparison.Ordinal)))
            findings.Add(new(w, [w[..w.IndexOf(claimed, StringComparison.Ordinal)]]));
        foreach (var (level, metrics) in facts.Levels)
        {
            foreach (var cycle in metrics.Cycles)
            {
                var sb = new StringBuilder($"cycle among the children of {level}: {string.Join(" → ", cycle)} → {cycle[0]}");
                var types = new List<string>();
                for (var i = 0; i < cycle.Count; i++)
                {
                    var (from, to) = (cycle[i], cycle[(i + 1) % cycle.Count]);
                    var pairs = Explain.Pairs(facts, from, to);
                    sb.Append($"\n{from} → {to}: {pairs.Sum(p => p.Count)} reference(s), from {string.Join(", ", pairs.Take(4).Select(p => $"{p.FromType} → {p.ToType} ({p.Count})"))}");
                    types.AddRange(pairs.Take(4).SelectMany(p => new[] { p.FromType, p.ToType }));
                }
                findings.Add(new(sb.ToString(), types));
            }
        }
        foreach (var (view, metrics) in facts.Views)
        {
            foreach (var cycle in metrics.Cycles) findings.Add(new($"cycle among the groups of view {view}: {string.Join(" → ", cycle)} → {cycle[0]}", []));
        }
        if (!largeLeaves) return findings;
        foreach (var (id, node) in facts.Map.Nodes)
        {
            if (node.IsLeaf && facts.Nodes.TryGetValue(id, out var m) && m.Types > LargeLeaf)
                findings.Add(new($"leaf {id} holds {m.Types} types, too many for a view to regroup; split it", facts.Types.Where(t => t.Leaf == id).Select(t => t.Name).ToList()));
        }
        return findings;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0 ? v.GetString() : null;
}
