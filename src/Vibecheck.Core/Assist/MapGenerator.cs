using System.Diagnostics;
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

/// <summary>
/// Holds one thing the checker reports about a map, the full names of the types it concerns, and whether it is a cycle
/// in the code, which comes from a <see cref="Knot"/> too large for one leaf and which no map removes.
/// </summary>
/// <param name="Cycle">For a cycle among a level's children, the key that <see cref="LocalRepair.Key"/> returns for it.</param>
/// <param name="Entangled">A value indicating whether the finding is a cycle that <see cref="LocalRepair"/> calls entangled.</param>
internal sealed record Finding(string Text, IReadOnlyList<string> Types, bool InCode = false, string? Cycle = null, bool Entangled = false);

/// <summary>
/// Runs the bootstrapping loop of <c>docs/vibecheck-spec.md</c> section 5.3 without a person in it. Claude proposes a
/// concept map from a listing of the solution, the checker reports cycles, unsorted types, ambiguous bindings and
/// oversized leaves, and Claude edits the map until the checker finds nothing or the rounds run out. Before each check,
/// the stray types of each small <see cref="Knot"/> are moved into one leaf, and <see cref="LocalRepair"/> moves the
/// types behind the cycles that a few moves break, without asking Claude. See <c>docs/review-workflow.md</c> section 4.4
/// and <c>docs/map-cycles.md</c>.
/// </summary>
public static class MapGenerator
{
    /// <summary>The most rounds of proposing and revising that one generation runs.</summary>
    public const int MaxRounds = 3;

    /// <summary>The most edges of a cycle's cut set that a finding lists.</summary>
    public const int MaxCutEdges = 12;

    /// <summary>The most edges a cycle's cut set may have for <see cref="LocalRepair"/> to repair it.</summary>
    public const int RepairEdges = 3;

    /// <summary>The most types that the cheapest candidate for a cycle may move, with their units, for <see cref="LocalRepair"/> to repair it.</summary>
    public const int RepairTypes = 6;

    /// <summary>Gets how long <see cref="LocalRepair"/> may search in one round.</summary>
    public static readonly TimeSpan RepairBudget = TimeSpan.FromSeconds(60);

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
    /// fewest findings that a map can fix, then the fewest findings, preferring the later of two that tie. Each revision edits that
    /// proposal, and is told only the findings that a map can fix. A revision that fails, or takes longer than
    /// <see cref="RequestTimeout"/>, ends the rounds and keeps the best proposal so far. While Claude works, the progress
    /// says every few seconds how long the request has run and how much Claude has thought and written. The rounds also
    /// stop when a usable map's only findings that a map can fix are entangled cycles that the previous usable map also
    /// had, since Claude has already failed to break them. The log receives each finding of every round, each type moved
    /// to keep a knot in one leaf or to break a cycle, each type the fit guard held in place, each cycle that local repair
    /// left and why, what local repair did in each round, how long each run of the checker took, and each reason a proposal could not be used as a map.
    /// </summary>
    /// <exception cref="ClaudeException">The proposal failed, or no proposal could be used as a map.</exception>
    public static Task<GeneratedMap> GenerateAsync(ArchitectureMap skeleton, AnalysisResult analysis, IClaude claude, IProgress<string>? progress = null, CancellationToken ct = default, string? model = null, Action<string>? log = null) =>
        GenerateAsync(skeleton, analysis, claude, progress, ct, model, log, MaxListing, RepairBudget);

    /// <summary>
    /// Proposes and revises a concept map as the public overload does, listing the solution by namespace when its
    /// unit listing is longer than <paramref name="maxListing"/> characters, and searching for local repairs for at most
    /// <paramref name="repairBudget"/> per round.
    /// </summary>
    /// <exception cref="ClaudeException">The proposal failed, or no proposal could be used as a map.</exception>
    internal static async Task<GeneratedMap> GenerateAsync(ArchitectureMap skeleton, AnalysisResult analysis, IClaude claude, IProgress<string>? progress, CancellationToken ct, string? model, Action<string>? log, int maxListing, TimeSpan repairBudget)
    {
        var errors = new List<string>();
        var (included, _) = Exclusion.Parse(skeleton, errors).Apply(analysis);
        var units = Units.Build(included);
        var (byNamespace, format, listing) = Listing(included, units, maxListing);
        var knots = Knots.Find(included);
        var knotSection = KnotSection(knots, included, byNamespace);
        var repair = new LocalRepair(units, knots, repairBudget);
        var rules = Prompts.Fill(Prompts.Read("map-rules.md"),
            ("externals", skeleton.Externals.Count == 0 ? "none" : string.Join(", ", skeleton.Externals.Keys.Select(k => $"`{k}`"))),
            ("exclude", string.Join(", ", skeleton.Exclude.Concat(skeleton.Tests).Select(e => $"`{e}`"))));
        var (mapSchema, editSchema) = (Prompts.Read("map.schema.json"), Prompts.Read("map-edits.schema.json"));
        string Propose(IReadOnlyList<string> rejected) => Prompts.Fill(Prompts.Read("propose-map.md"),
            ("rules", rules), ("format", format), ("knots", knotSection), ("rejected", Rejected(rejected)), ("listing", listing));

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
        var entangledBefore = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 1; ; round++)
        {
            var (map, text, found, gathered, checks, repaired) = Check(candidate, problems, analysis, !byNamespace, knots, repair);
            if (checks.Count > 0) log?.Invoke($"round {round} ran the checker {checks.Count} time(s): {string.Join(", ", checks.Select(c => $"{c.TotalMilliseconds:F0} ms"))}");
            findings = found.Select(f => f.Text).ToList();
            var fixable = found.Count(f => !f.InCode);
            if (gathered.Count > 0)
            {
                progress?.Report($"round {round}: moved {gathered.Count} type(s) to keep types that reference each other in a loop in one leaf");
                foreach (var (type, leaf) in gathered) log?.Invoke($"round {round} moved {type} to {leaf}, to keep a knot in one leaf");
            }
            if (repaired is not null)
            {
                if (repaired.Repaired > 0) progress?.Report($"round {round}: broke {repaired.Repaired} cycle(s) by moving {repaired.Moves.Count} type(s)");
                foreach (var m in repaired.Moves) log?.Invoke($"round {round} moved {m.Type} to {m.Leaf}, to break a cycle among the children of {m.Level}");
                foreach (var h in repaired.Held) log?.Invoke($"round {round} fit guard held {h.Type} in {h.Leaf}, for a cycle among the children of {h.Level}");
                foreach (var (cycle, why) in repaired.Left) log?.Invoke($"round {round} left the cycle among the children of {cycle}, because {why}");
                var cycles = found.Where(f => f.Cycle is not null && !f.InCode).ToList();
                log?.Invoke($"round {round} local repair broke {repaired.Repaired} cycle(s) and moved {repaired.Moves.Count} type(s) in {repaired.Elapsed.TotalMilliseconds:F0} ms"
                    + $"{(repaired.OutOfTime ? ", and ran out of time" : "")}; {cycles.Count(f => f.Entangled)} entangled cycle(s) and {cycles.Count(f => !f.Entangled)} other cycle(s) remain");
            }
            if (map is null)
            {
                progress?.Report($"round {round}: the map cannot be used, for {found.Count} reason(s)");
                foreach (var f in findings) log?.Invoke($"round {round} was rejected: {f}");
            }
            else
            {
                progress?.Report(found.Count == 0 ? $"round {round}: the checker found nothing to fix"
                    : fixable == 0 ? $"round {round}: the checker reports only {found.Count} cycle(s) in the code, which no map removes"
                    : $"round {round}: the checker reports {found.Count} finding(s)");
                foreach (var f in found) log?.Invoke($"round {round} finding{(f.InCode ? " in the code" : "")}: {f.Text}");
            }
            var bestFixable = bestFindings.Count(f => !f.InCode);
            if (map is not null && (best is null || fixable < bestFixable || (fixable == bestFixable && found.Count <= best.Findings.Count)))
                (best, bestFindings) = (new GeneratedMap(map, text, round, findings), found);
            // A usable revision that fixes nothing gets no further round, since another would only spend the reviewer's
            // usage. Cycles in the code do not count, since no revision removes them. An unusable revision gets another round.
            var stalled = map is not null && fixable >= previous;
            // Nor does a usable map whose only fixable findings are entangled cycles that the previous usable map had too,
            // since Claude has already failed to break them; they wait for the reviewer.
            var entangled = found.Where(f => f.Entangled).Select(f => f.Cycle!).ToHashSet(StringComparer.Ordinal);
            var stuck = map is not null && fixable > 0 && found.Where(f => !f.InCode).All(f => f.Entangled && entangledBefore.Contains(f.Cycle!));
            if (stuck) progress?.Report($"round {round}: only entangled cycles that the last revision did not break remain, so they are left for the reviewer");
            if (fixable == 0 || round == MaxRounds || stalled || stuck) break;
            previous = map is null ? previous : fixable;
            if (map is not null) entangledBefore = entangled;
            var rejected = map is null ? findings : [];

            // Until some proposal is usable there is no map to edit, so Claude proposes a whole map again.
            progress?.Report($"{(best is null ? "proposing" : "revising")} the concept map (round {round + 1})");
            string prompt;
            if (best is null) prompt = Propose(rejected);
            else
            {
                var sent = bestFindings.Where(f => !f.InCode).ToList();
                var named = sent.SelectMany(f => f.Types).ToHashSet(StringComparer.Ordinal);
                var (_, context, types) = named.Count == 0 ? (false, "", "The findings name no types.") : Listing(included, units, maxListing, named);
                prompt = Prompts.Fill(Prompts.Read("revise-map.md"), ("rules", rules), ("map", Describe(best.Map)),
                    ("findings", string.Join("\n\n", sent.Select(f => "- " + f.Text.Replace("\n", "\n  ")))),
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
        var (map, text, findings, _, _, _) = Check(Build(skeleton, answer, problems), problems, analysis, largeLeaves: true, knots: [], repair: null);
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
    /// Checks a map, after moving the stray types of each small knot into one leaf and, when a search is given, after
    /// repairing the cycles that a few moves break. Returns a null map when it cannot be used at all, with the problems
    /// and the reasons as findings. Otherwise returns the map as the reader reads it back, its canonical text, the
    /// problems and what the checker reports about it, the moves that kept knots whole, and what the repair did. Also
    /// returns how long each run of <see cref="Checker.Run"/> took, since the knot pass and the repair check the map again.
    /// </summary>
    /// <param name="largeLeaves">A value indicating whether leaves too large for a view to regroup are reported.</param>
    /// <param name="knots">The solution's knots.</param>
    /// <param name="repair">The search that repairs cycles, or <see langword="null"/> to repair none.</param>
    private static (ArchitectureMap? Map, string Text, IReadOnlyList<Finding> Findings, IReadOnlyList<(string Type, string Leaf)> Gathered, IReadOnlyList<TimeSpan> Checks, RepairOutcome? Repaired) Check(
        ArchitectureMap map, List<string> problems, AnalysisResult analysis, bool largeLeaves, IReadOnlyList<Knot> knots, LocalRepair? repair)
    {
        IEnumerable<Finding> Plain(IEnumerable<string> texts) => texts.Select(t => new Finding(t, []));
        var checks = new List<TimeSpan>();
        (CheckedMap? Checked, string Text, List<Finding> Failures) Run(ArchitectureMap map)
        {
            var text = MapWriter.Write(map);
            var errors = new List<string>();
            var parsed = MapReader.Read(text, errors);
            if (parsed is null || errors.Count > 0) return (null, text, [.. Plain(problems), .. Plain(errors)]);
            var watch = Stopwatch.StartNew();
            var facts = Checker.Run(parsed, analysis, new CheckOptions { MapText = text });
            checks.Add(watch.Elapsed);
            if (!facts.Passed) return (null, text, [.. Plain(problems), .. Plain(facts.Violations.Select(v => v.Message))]);
            return (new CheckedMap(parsed, text, facts), text, []);
        }

        var (current, text, failures) = Run(map);
        if (current is null) return (null, text, failures, [], checks, null);
        var gathered = Knots.Gathering(current.Facts, knots);
        if (gathered.Count > 0)
        {
            var moved = current.Map.Clone();
            foreach (var (type, leaf) in gathered) MapEdits.Move(moved, type, leaf);
            if (Run(moved).Checked is { } again) current = again;
            else gathered = [];
        }
        RepairOutcome? repaired = null;
        if (repair is not null)
        {
            repaired = repair.Run(current, m => Run(m).Checked, f => Findings(f, largeLeaves, Knots.InCode(f, knots)).Count(x => !x.InCode));
            current = repaired.Map;
        }
        var facts = current.Facts;
        return (current.Map, current.Text, [.. Plain(problems), .. Findings(facts, largeLeaves, Knots.InCode(facts, knots), repair?.Entangled(facts))], gathered, checks, repaired);
    }

    /// <summary>
    /// Returns the section of the proposal prompt that lists the solution's knots, or an empty string when there is
    /// none to list. A knot within one namespace is not listed, since a namespace pattern keeps it whole. A solution listed by namespace
    /// lists only its large knots, by namespace, and leaves its small knots to the knot pass, which keeps the prompt
    /// from growing with the number of types.
    /// </summary>
    private static string KnotSection(IReadOnlyList<Knot> knots, AnalysisResult included, bool byNamespace)
    {
        var namespaceOf = included.Types.ToDictionary(t => t.Name, t => t.Namespace, StringComparer.Ordinal);
        string Spread(Knot k)
        {
            var counts = k.Types.GroupBy(t => namespaceOf[t], StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"`{g.Key}` ({g.Count()})").ToList();
            return $"{k.Types.Count} types in {string.Join(", ", counts.Take(12))}{(counts.Count > 12 ? ", …" : "")}";
        }
        var listed = knots.Where(k => k.Types.Select(t => namespaceOf[t]).Distinct(StringComparer.Ordinal).Count() > 1).ToList();
        var small = byNamespace ? [] : listed.Where(k => k.Small).Select(k => "- " + string.Join(", ", k.Types.Select(t => $"`{t}`"))).ToList();
        var large = listed.Where(k => !k.Small).Select(k => "- " + Spread(k)).ToList();
        if (small.Count == 0 && large.Count == 0) return "";
        return Prompts.Fill(Prompts.Read("knots.md"),
            ("small", small.Count == 0 ? "" : $"Bind the types of each of these sets to one leaf.\n\n{string.Join("\n", small)}\n\n"),
            ("large", large.Count == 0 ? "" : "Each of these sets holds more types than one leaf should, so the cycle it makes cannot be avoided. "
                + $"Place its types under one container, so that the cycle stays among that container's children.\n\n{string.Join("\n", large)}\n\n"));
    }

    /// <summary>
    /// Returns what the checker's facts say is wrong with a usable map: unsorted types, types two leaves claim equally,
    /// cycles among a level's children with the edges whose removal breaks each and the type pairs behind those edges,
    /// cycles among a view's groups with the edges whose removal breaks each, and, when
    /// asked for, leaves too large for a view to regroup. Each finding names the types it concerns, and a cycle that is in
    /// the code or entangled says so.
    /// </summary>
    /// <param name="facts">The checker's facts about the map.</param>
    /// <param name="largeLeaves">A value indicating whether leaves too large for a view to regroup are reported.</param>
    /// <param name="inCode">A function that tells whether a level's cycle is in the code, as <see cref="Knots.InCode"/> returns it, or <see langword="null"/> when none is.</param>
    /// <param name="entangled">A function that tells whether a level's cycle is entangled, as <see cref="LocalRepair.Entangled"/> returns it, or <see langword="null"/> when none is.</param>
    internal static List<Finding> Findings(Facts facts, bool largeLeaves = true, Func<string, IReadOnlyList<string>, bool>? inCode = null, Func<string, IReadOnlyList<string>, bool>? entangled = null)
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
                var cut = Checker.CutSet(cycle, metrics.Edges);
                var sb = new StringBuilder($"cycle among the children of {level}: {string.Join(", ", cycle)}");
                sb.Append($"\nRemoving these {cut.Count} edge(s) breaks it, lightest first:");
                var types = new List<string>();
                foreach (var e in cut.Take(MaxCutEdges))
                {
                    var pairs = Explain.Pairs(facts, e.From, e.To);
                    sb.Append($"\n{e.From} → {e.To}: {e.Count} reference(s), from {string.Join(", ", pairs.Take(4).Select(p => $"{p.FromType} → {p.ToType} ({p.Count})"))}");
                    types.AddRange(pairs.Take(4).SelectMany(p => new[] { p.FromType, p.ToType }));
                }
                var code = inCode?.Invoke(level, cycle) == true;
                var tangled = !code && entangled?.Invoke(level, cycle) == true;
                if (cut.Count > MaxCutEdges) sb.Append($"\n…and {cut.Count - MaxCutEdges} more edge(s){(tangled ? "." : "; so many usually means the code does not separate these children, and merging some of them breaks the cycle.")}");
                if (code) sb.Append("\nThis cycle is in the code: it comes from types that reference each other in a loop and are too many for one leaf.");
                if (tangled) sb.Append("\nThis cycle is entangled: these children reference each other in too many ways for a few moves to separate them, so merging some of them is the likely fix.");
                findings.Add(new(sb.ToString(), types, code, LocalRepair.Key(level, cycle), tangled));
            }
        }
        foreach (var (view, metrics) in facts.Views)
        {
            foreach (var cycle in metrics.Cycles)
            {
                var cut = Checker.CutSet(cycle, metrics.Edges);
                findings.Add(new($"cycle among the groups of view {view}: {string.Join(", ", cycle)}; removing "
                    + $"{string.Join(", ", cut.Take(MaxCutEdges).Select(e => $"{e.From} → {e.To} ({e.Count})"))}{(cut.Count > MaxCutEdges ? $" and {cut.Count - MaxCutEdges} more" : "")} breaks it", []));
            }
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
