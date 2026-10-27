using System.Text.Json;
using Vibecheck.Model;

namespace Vibecheck.Assist;

/// <summary>
/// Applies the edits that Claude answers a map revision with, so that a revision is as long as its changes rather than
/// as long as the map. An edit removes nodes, adds or changes nodes, adds or changes the groups of a view, or moves a
/// type to a leaf by its exact name. See <c>docs/review-workflow.md</c> section 4.4.
/// </summary>
public static class MapEdits
{
    /// <summary>
    /// Returns a copy of a map with an answer's edits applied, in this order: removals, nodes, groups, then moves. A move
    /// whose type names a leaf and whose leaf names no node is applied with the two swapped. An edit that cannot be
    /// applied is skipped, and the reason is added to the problems.
    /// </summary>
    /// <param name="map">The map to edit, which is not changed.</param>
    /// <param name="answer">The answer, in the shape of <c>map-edits.schema.json</c>.</param>
    /// <param name="problems">The list that receives a reason for each edit that is skipped.</param>
    public static ArchitectureMap Apply(ArchitectureMap map, JsonElement answer, List<string> problems)
    {
        var copy = map.Clone();
        foreach (var id in Strings(answer, "remove"))
        {
            var removed = copy.Nodes.Keys.Where(k => Identifiers.IsUnder(k, id)).ToList();
            if (removed.Count == 0) problems.Add($"the edit removes node \"{id}\", which does not exist");
            foreach (var k in removed) copy.Nodes.Remove(k);
        }

        var joining = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in Items(answer, "nodes"))
        {
            var id = node.GetProperty("id").GetString()!;
            var bind = node.TryGetProperty("bind", out var b) ? b.EnumerateArray().Select(p => p.GetString()!).ToList() : null;
            if (copy.Nodes.TryGetValue(id, out var old))
            {
                if (bind is not null && !old.IsLeaf) { problems.Add($"the edit gives container \"{id}\" bindings; remove it and add it again as a leaf"); continue; }
                copy.Nodes[id] = old with
                {
                    Name = Text(node, "name") ?? old.Name, Purpose = Text(node, "purpose") ?? old.Purpose,
                    Category = Text(node, "category") ?? old.Category, Bind = bind ?? old.Bind,
                };
            }
            else if (Text(node, "name") is not { } name) { problems.Add($"the edit adds node \"{id}\" without a name"); continue; }
            else copy.Nodes[id] = new NodeSpec { Name = name, Purpose = Text(node, "purpose"), Category = Text(node, "category"), Bind = bind };
            if (Text(node, "group") is { } group) joining[id] = group;
        }

        foreach (var g in Items(answer, "groups"))
        {
            var (viewId, id) = (g.GetProperty("view").GetString()!, g.GetProperty("id").GetString()!);
            if (!copy.Views.TryGetValue(viewId, out var view)) { problems.Add($"the edit changes group \"{id}\" of view \"{viewId}\", which does not exist"); continue; }
            var groups = new SortedDictionary<string, GroupSpec>(view.Groups, StringComparer.Ordinal);
            groups[id] = groups.TryGetValue(id, out var old)
                ? old with { Name = Text(g, "name") ?? old.Name, Purpose = Text(g, "purpose") ?? old.Purpose }
                : new GroupSpec { Name = Text(g, "name") ?? id, Purpose = Text(g, "purpose"), Leaves = [] };
            copy.Views[viewId] = view with { Groups = groups };
        }

        foreach (var m in Items(answer, "moves"))
        {
            var (type, leafId) = (m.GetProperty("type").GetString()!, m.GetProperty("leaf").GetString()!);
            // A move whose type names a leaf and whose leaf names no node has its two fields swapped, so it is applied
            // the other way round rather than lost. See issue #11.
            if (copy.Nodes.TryGetValue(type, out var named) && named.IsLeaf && !copy.Nodes.ContainsKey(leafId)) (type, leafId) = (leafId, type);
            if (!Move(copy, type, leafId)) problems.Add($"the edit moves {type} to \"{leafId}\", which is not a leaf");
        }

        DropStaleLeaves(copy);
        foreach (var id in copy.Nodes.Where(kv => kv.Value.IsLeaf).Select(kv => kv.Key).ToList())
            JoinViews(copy, id, (viewId, view) => joining.TryGetValue(id, out var g) && view.Groups.ContainsKey(g) ? g : null);
        foreach (var (viewId, view) in copy.Views.ToList())
            copy.Views[viewId] = view with { Groups = new SortedDictionary<string, GroupSpec>(view.Groups.Where(g => g.Value.Leaves.Count > 0).ToDictionary(), StringComparer.Ordinal) };
        return copy;
    }

    /// <summary>
    /// Places a leaf in one group of every view whose level holds it, since a view must list every leaf under its
    /// level. Where a function names a group for the view, the leaf leaves any other group and joins that one. Where
    /// it names none, a leaf already in a group stays there, and a leaf in none joins the group holding most of its
    /// siblings, or else the view's first group.
    /// </summary>
    /// <param name="map">The map whose views change.</param>
    /// <param name="leaf">The identifier of the leaf.</param>
    /// <param name="named">A function that returns the group a view's leaf should join, or <see langword="null"/> when none is named.</param>
    public static void JoinViews(ArchitectureMap map, string leaf, Func<string, ViewSpec, string?> named)
    {
        foreach (var (viewId, view) in map.Views.Where(v => Identifiers.IsUnder(leaf, v.Value.Level)).ToList())
        {
            var chosen = named(viewId, view);
            if (chosen is null && view.Groups.Values.Any(g => g.Leaves.Contains(leaf))) continue;
            var group = chosen
                ?? view.Groups.OrderByDescending(g => g.Value.Leaves.Count(l => Identifiers.ParentOrRoot(l) == Identifiers.ParentOrRoot(leaf)))
                    .Select(g => g.Key).FirstOrDefault();
            if (group is null) continue;
            var groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal);
            foreach (var (id, g) in view.Groups) groups[id] = g with { Leaves = g.Leaves.Where(l => l != leaf).ToList() };
            groups[group] = groups[group] with { Leaves = [.. groups[group].Leaves, leaf] };
            map.Views[viewId] = view with { Groups = groups };
        }
    }

    /// <summary>
    /// Binds a type to a leaf by its exact full name, and takes that exact name out of any other leaf. Returns
    /// <see langword="false"/>, and changes nothing, when the identifier does not name a leaf.
    /// </summary>
    /// <param name="map">The map to change in place.</param>
    /// <param name="type">The type's full name.</param>
    /// <param name="leafId">The identifier of the leaf that receives the type.</param>
    internal static bool Move(ArchitectureMap map, string type, string leafId)
    {
        if (!map.Nodes.TryGetValue(leafId, out var leaf) || !leaf.IsLeaf) return false;
        foreach (var (id, other) in map.Nodes.Where(kv => kv.Value.IsLeaf && kv.Value.Bind!.Contains(type)).ToList())
            map.Nodes[id] = other with { Bind = other.Bind!.Where(p => p != type).ToList() };
        map.Nodes[leafId] = map.Nodes[leafId] with { Bind = [.. map.Nodes[leafId].Bind!, type] };
        return true;
    }

    /// <summary>Removes from each view's groups the identifiers that no longer name a leaf.</summary>
    private static void DropStaleLeaves(ArchitectureMap map)
    {
        foreach (var (viewId, view) in map.Views.ToList())
        {
            var groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal);
            foreach (var (id, g) in view.Groups) groups[id] = g with { Leaves = g.Leaves.Where(l => map.Nodes.TryGetValue(l, out var n) && n.IsLeaf).ToList() };
            map.Views[viewId] = view with { Groups = groups };
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement answer, string name) =>
        answer.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    private static IEnumerable<string> Strings(JsonElement answer, string name) => Items(answer, name).Select(e => e.GetString()!);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0 ? v.GetString() : null;
}
