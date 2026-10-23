using Archmap.Model;

namespace Archmap.Checking;

/// <summary>
/// Applies the edits the checker proposes to a copy of a map. Proposals only ever make
/// the map describe the code: they add missing edges, remove dead edges and add leaves
/// for unmapped files. They never relax a policy, label an edge or assert one.
/// </summary>
public static class Proposals
{
    public static ArchMap Apply(ArchMap map, IEnumerable<ProposalFact> proposals)
    {
        var copy = map.Clone();
        foreach (var p in proposals)
        {
            switch (p.Op)
            {
                case "add-edge":
                    if (copy.FindEdge(p.From!, p.To!) is null) copy.Edges.Add(new EdgeSpec { From = p.From!, To = p.To! });
                    break;
                case "remove-edge":
                    copy.Edges.RemoveAll(e => e.From == p.From && e.To == p.To);
                    break;
                case "add-leaf":
                    if (!copy.Nodes.ContainsKey(p.Node!)) copy.Nodes[p.Node!] = new NodeSpec { Name = p.Name ?? p.Node!, Bind = p.Bind };
                    break;
                default:
                    throw new ArgumentException($"unknown proposal operation \"{p.Op}\"");
            }
        }
        return copy;
    }
}
