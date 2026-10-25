using System.Text;
using System.Text.Json;
using Vibecheck.Analysis;
using Vibecheck.Bootstrap;
using Vibecheck.Checking;
using Vibecheck.Format;
using Vibecheck.Model;

namespace Vibecheck.Assist;

/// <summary>Holds a map that Claude proposed and the checker verified: the map, its canonical text, the round it came from, and what the checker still reports about it.</summary>
public sealed record GeneratedMap(ArchitectureMap Map, string Text, int Round, IReadOnlyList<string> Findings);

/// <summary>
/// Runs the bootstrapping loop of <c>docs/vibecheck-spec.md</c> section 5.3 without a person in it. Claude proposes a
/// concept map from the unit listing, the checker reports cycles, unsorted types, ambiguous bindings and oversized
/// leaves, and Claude revises the map until the checker finds nothing or the rounds run out. See
/// <c>docs/review-workflow.md</c> section 4.4.
/// </summary>
public static class MapGenerator
{
    /// <summary>The most rounds of proposing and revising that one generation runs.</summary>
    public const int MaxRounds = 3;

    /// <summary>A leaf with more types than this is reported as too large for a view to regroup.</summary>
    public const int LargeLeaf = 60;

    /// <summary>
    /// Proposes a concept map for an analysis and revises it against the checker's findings. The skeleton supplies the
    /// solution, the exclusions, the test types and the externals, which the generated map keeps. Returns the usable proposal with the
    /// fewest findings, preferring the later of two that tie.
    /// </summary>
    /// <exception cref="ClaudeException">Claude failed, or no proposal could be used as a map.</exception>
    public static async Task<GeneratedMap> GenerateAsync(ArchitectureMap skeleton, AnalysisResult analysis, IClaude claude, IProgress<string>? progress = null, CancellationToken ct = default, string? model = null)
    {
        var errors = new List<string>();
        var (included, _) = Exclusion.Parse(skeleton, errors).Apply(analysis);
        var listing = Units.Listing(Units.Build(included), included.Types.Count);
        var rules = Prompts.Fill(Prompts.Read("map-rules.md"),
            ("externals", skeleton.Externals.Count == 0 ? "none" : string.Join(", ", skeleton.Externals.Keys.Select(k => $"`{k}`"))),
            ("exclude", string.Join(", ", skeleton.Exclude.Concat(skeleton.Tests).Select(e => $"`{e}`"))));
        var schema = Prompts.Read("map.schema.json");

        progress?.Report($"proposing a concept map from {included.Types.Count} types");
        var answer = await claude.AskAsync(new ClaudeRequest(Prompts.Fill(Prompts.Read("propose-map.md"), ("rules", rules), ("listing", listing)), schema) { Model = model }, ct);
        GeneratedMap? best = null;
        IReadOnlyList<string> findings = [];
        var previous = int.MaxValue;
        for (var round = 1; ; round++)
        {
            (var map, var text, findings) = Evaluate(skeleton, answer, analysis);
            if (map is not null && (best is null || findings.Count <= best.Findings.Count)) best = new GeneratedMap(map, text, round, findings);
            progress?.Report(findings.Count == 0 ? $"round {round}: the checker found nothing to fix"
                : $"round {round}: the checker reports {findings.Count} finding(s)");
            // A usable revision that fixes nothing usually meets a cycle in the code itself, which no grouping removes,
            // so another round would only spend the reviewer's usage. An unusable revision gets another round.
            var stalled = map is not null && findings.Count >= previous;
            if (findings.Count == 0 || round == MaxRounds || stalled) break;
            previous = map is null ? previous : findings.Count;
            progress?.Report($"revising the concept map (round {round + 1})");
            var prompt = Prompts.Fill(Prompts.Read("revise-map.md"), ("rules", rules), ("listing", listing),
                ("previous", JsonSerializer.Serialize(answer, new JsonSerializerOptions { WriteIndented = true })),
                ("findings", string.Join("\n\n", findings.Select(f => "- " + f.Replace("\n", "\n  ")))));
            answer = await claude.AskAsync(new ClaudeRequest(prompt, schema) { Model = model }, ct);
        }
        return best ?? throw new ClaudeException("Claude proposed no usable map: " + string.Join("; ", findings.Take(5)));
    }

    /// <summary>
    /// Builds the map an answer describes and checks it. Returns a null map when it cannot be used at all, with the
    /// reasons as findings, and otherwise the map, its canonical text and what the checker reports about it.
    /// </summary>
    internal static (ArchitectureMap? Map, string Text, IReadOnlyList<string> Findings) Evaluate(ArchitectureMap skeleton, JsonElement answer, AnalysisResult analysis)
    {
        var problems = new List<string>();
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

        var text = MapWriter.Write(map);
        var errors = new List<string>();
        var parsed = MapReader.Read(text, errors);
        if (parsed is null || errors.Count > 0) return (null, text, [.. problems, .. errors]);
        var facts = Checker.Run(parsed, analysis, new CheckOptions { MapText = text });
        if (!facts.Passed) return (null, text, [.. problems, .. facts.Violations.Select(v => v.Message)]);
        return (parsed, text, [.. problems, .. Findings(facts)]);
    }

    /// <summary>
    /// Returns what the checker's facts say is wrong with a usable map: unsorted types, types two leaves claim equally,
    /// cycles among a level's children with the type pairs behind each edge, cycles among a view's groups, and leaves
    /// too large for a view to regroup.
    /// </summary>
    internal static List<string> Findings(Facts facts)
    {
        var findings = new List<string>();
        var unsorted = facts.Types.Where(t => t.Leaf == ArchitectureMap.Unsorted).Select(t => t.Name).ToList();
        if (unsorted.Count > 0)
            findings.Add($"{unsorted.Count} type(s) are unsorted, because no leaf binds them: {string.Join(", ", unsorted.Take(60))}{(unsorted.Count > 60 ? ", …" : "")}");
        findings.AddRange(facts.Warnings.Where(w => w.Contains("is claimed equally", StringComparison.Ordinal)));
        foreach (var (level, metrics) in facts.Levels)
        {
            foreach (var cycle in metrics.Cycles)
            {
                var sb = new StringBuilder($"cycle among the children of {level}: {string.Join(" → ", cycle)} → {cycle[0]}");
                for (var i = 0; i < cycle.Count; i++)
                {
                    var (from, to) = (cycle[i], cycle[(i + 1) % cycle.Count]);
                    var pairs = Explain.Pairs(facts, from, to);
                    sb.Append($"\n{from} → {to}: {pairs.Sum(p => p.Count)} reference(s), from {string.Join(", ", pairs.Take(4).Select(p => $"{p.FromType} → {p.ToType} ({p.Count})"))}");
                }
                findings.Add(sb.ToString());
            }
        }
        foreach (var (view, metrics) in facts.Views)
        {
            foreach (var cycle in metrics.Cycles) findings.Add($"cycle among the groups of view {view}: {string.Join(" → ", cycle)} → {cycle[0]}");
        }
        foreach (var (id, node) in facts.Map.Nodes)
        {
            if (node.IsLeaf && facts.Nodes.TryGetValue(id, out var m) && m.Types > LargeLeaf)
                findings.Add($"leaf {id} holds {m.Types} types, too many for a view to regroup; split it");
        }
        return findings;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0 ? v.GetString() : null;
}
