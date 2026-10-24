using System.Text;
using System.Text.Json;
using Archmap.Analysis;
using Archmap.Bootstrap;
using Archmap.Checking;
using Archmap.Model;

namespace Archmap.Assist;

/// <summary>Holds a map with types placed into it, and how many types were placed and left unsorted.</summary>
public sealed record Placement(ArchMap Map, int Placed, int Unsorted);

/// <summary>
/// Places the types a generated map leaves unsorted. Claude names a leaf for each unsorted type, adding a leaf where none
/// fits, and each type is then bound to its leaf by its exact name. The units the types belong to are listed as context,
/// since a helper folds into the unit of the type that uses it, and that unit is usually placed already. See
/// <c>docs/review-workflow.md</c> section 4.5.
/// </summary>
public static class TypePlacer
{
    /// <summary>
    /// Returns the map with the unsorted types of an analysis placed, or <see langword="null"/> when the map leaves no
    /// type unsorted. A placement that names an unknown type or leaf is ignored, and its types stay unsorted.
    /// </summary>
    /// <exception cref="ClaudeException">Claude failed, or the placed map cannot be used.</exception>
    public static async Task<Placement?> PlaceAsync(ArchMap map, AnalysisResult analysis, IClaude claude, IProgress<string>? progress = null, CancellationToken ct = default, string? model = null)
    {
        var facts = Checker.Run(map, analysis);
        var unsorted = facts.Types.Where(t => t.Leaf == ArchMap.Unsorted).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (unsorted.Count == 0) return null;
        var (included, _) = Exclusion.Parse(map.Exclude, []).Apply(analysis);
        var units = Units.Build(included).Where(u => u.Members.Any(unsorted.Contains)).ToList();

        progress?.Report($"placing {unsorted.Count} unsorted type(s)");
        var prompt = Prompts.Fill(Prompts.Read("place-types.md"),
            ("leaves", Leaves(map, facts)),
            ("types", string.Join("\n", unsorted.Order(StringComparer.Ordinal).Select(t => "- " + t))),
            ("listing", Units.Listing(units, units.Sum(u => u.Members.Count))));
        var answer = await claude.AskAsync(new ClaudeRequest(prompt, Prompts.Read("placement.schema.json")) { Model = model }, ct);

        var placed = Apply(map, unsorted, answer);
        var check = Checker.Run(placed, analysis);
        if (!check.Passed) throw new ClaudeException("the placement made the map unusable: " + string.Join("; ", check.Violations.Select(v => v.Message)));
        var left = check.Types.Count(t => t.Leaf == ArchMap.Unsorted);
        return new Placement(placed, unsorted.Count - left, left);
    }

    /// <summary>
    /// Returns a copy of a map with an answer's new leaves added and each placed type bound to its leaf by exact name.
    /// A placement of a type that is not unsorted, or into a node that is not a leaf, is ignored. New leaves that end up
    /// with no types are dropped. A new leaf joins one group of every view whose level holds it, since a view must
    /// list every leaf under its level: the group the answer names, or else the group holding most of the leaf's
    /// siblings, or else the view's first group.
    /// </summary>
    internal static ArchMap Apply(ArchMap map, ISet<string> unsorted, JsonElement answer)
    {
        var copy = map.Clone();
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in answer.GetProperty("newLeaves").EnumerateArray())
        {
            var id = leaf.GetProperty("id").GetString()!;
            var parent = Identifiers.ParentOrRoot(id);
            if (copy.Exists(id) || !Identifiers.IsValid(id) || !copy.IsContainer(parent)) continue;
            copy.Nodes[id] = new NodeSpec { Name = leaf.GetProperty("name").GetString()!, Purpose = leaf.GetProperty("purpose").GetString(), Bind = [] };
            added.Add(id);
        }
        foreach (var p in answer.GetProperty("placements").EnumerateArray())
        {
            var (type, leafId) = (p.GetProperty("type").GetString()!, p.GetProperty("leaf").GetString()!);
            if (!unsorted.Contains(type) || !copy.Nodes.TryGetValue(leafId, out var leaf) || !leaf.IsLeaf || leaf.Bind!.Contains(type)) continue;
            copy.Nodes[leafId] = leaf with { Bind = [.. leaf.Bind!, type] };
        }
        foreach (var id in added.Where(id => copy.Nodes[id].Bind!.Count == 0)) copy.Nodes.Remove(id);

        var named = answer.GetProperty("newLeaves").EnumerateArray()
            .SelectMany(l => (l.TryGetProperty("groups", out var g) ? g.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Select(g => (Leaf: l.GetProperty("id").GetString()!, View: g.GetProperty("view").GetString()!, Group: g.GetProperty("group").GetString()!)))
            .ToList();
        foreach (var id in added.Where(copy.Nodes.ContainsKey))
        {
            foreach (var (viewId, view) in copy.Views.Where(v => Identifiers.IsUnder(id, v.Value.Level)).ToList())
            {
                var group = named.Where(n => n.Leaf == id && n.View == viewId && view.Groups.ContainsKey(n.Group)).Select(n => n.Group).FirstOrDefault()
                    ?? view.Groups.OrderByDescending(g => g.Value.Leaves.Count(l => Identifiers.ParentOrRoot(l) == Identifiers.ParentOrRoot(id)))
                        .Select(g => g.Key).FirstOrDefault();
                if (group is null) continue;
                var groups = new SortedDictionary<string, GroupSpec>(view.Groups, StringComparer.Ordinal);
                groups[group] = groups[group] with { Leaves = [.. groups[group].Leaves, id] };
                copy.Views[viewId] = view with { Groups = groups };
            }
        }
        return copy;
    }

    /// <summary>Lists a map's leaves for the prompt, each with its name, its purpose and up to five of its types.</summary>
    private static string Leaves(ArchMap map, Facts facts)
    {
        var examples = facts.Types.Where(t => t.Leaf != ArchMap.Unsorted).GroupBy(t => t.Leaf, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => TypeNames.Plain(t.Name)).Take(5).ToList(), StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var (id, node) in map.Nodes.Where(kv => kv.Value.IsLeaf))
        {
            sb.Append($"- `{id}`, {node.Name}: {node.Purpose ?? "(no purpose given)"}");
            if (examples.TryGetValue(id, out var names)) sb.Append($" Holds {string.Join(", ", names)}.");
            sb.Append('\n');
        }
        var containers = map.Nodes.Where(kv => !kv.Value.IsLeaf).Select(kv => $"`{kv.Key}`").ToList();
        sb.Append($"\nThe containers, which hold leaves and no types of their own, are {(containers.Count == 0 ? "none" : string.Join(", ", containers))}.\n");
        foreach (var (id, view) in map.Views)
        {
            sb.Append($"\nThe view `{id}`, {view.Name}, regroups the leaves under `{view.Level}` into these groups:\n");
            foreach (var (groupId, group) in view.Groups) sb.Append($"- `{groupId}`, {group.Name}: {group.Purpose ?? string.Join(", ", group.Leaves)}\n");
        }
        return sb.ToString();
    }
}
