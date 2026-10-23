using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Archmap.Model;

namespace Archmap.Format;

/// <summary>
/// Writes <c>archmap.json</c> in canonical form. Each node, external, edge and policy
/// entry occupies one line, nodes and externals are sorted by identifier, edges are
/// sorted by source then target, and keys appear in the schema's order. The output
/// uses two-space indentation, LF line endings and ends with a newline.
/// </summary>
public static class MapWriter
{
    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Returns the canonical text of a map.</summary>
    public static string Write(ArchMap map)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"version\": ").Append(map.Version).Append(",\n");
        sb.Append("  \"solution\": ").Append(Str(map.Solution)).Append(",\n");

        sb.Append("  \"nodes\": {");
        AppendRecords(sb, map.Nodes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            Str(kv.Key) + ": " + Record(
                ("name", Str(kv.Value.Name)),
                ("purpose", OptStr(kv.Value.Purpose)),
                ("note", OptStr(kv.Value.Note)),
                ("edges", OptStr(kv.Value.Edges)),
                ("bind", kv.Value.Bind is null ? null : StrList(kv.Value.Bind)))));
        sb.Append("},\n");

        sb.Append("  \"externals\": {");
        AppendRecords(sb, map.Externals.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            Str(kv.Key) + ": " + Record(
                ("name", Str(kv.Value.Name)),
                ("purpose", OptStr(kv.Value.Purpose)),
                ("bind", StrList(kv.Value.Bind)))));
        sb.Append("},\n");

        sb.Append("  \"edges\": [");
        AppendRecords(sb, map.Edges
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .Select(e => Record(
                ("from", Str(e.From)),
                ("to", Str(e.To)),
                ("label", OptStr(e.Label)),
                ("payload", OptStr(e.Payload)),
                ("asserted", e.Asserted ? "true" : null))));
        sb.Append("],\n");

        sb.Append("  \"policies\": {\n");
        sb.Append("    \"root\": ").Append(Record(("edges", Str(map.Policies.Root.Edges)), ("externals", Str(map.Policies.Root.Externals)))).Append(",\n");
        sb.Append("    \"forbid\": [");
        if (map.Policies.Forbid.Count > 0)
        {
            sb.Append(' ');
            sb.Append(string.Join(", ", map.Policies.Forbid
                .OrderBy(f => f.From, StringComparer.Ordinal).ThenBy(f => f.To, StringComparer.Ordinal)
                .Select(f => Record(("from", Str(f.From)), ("to", Str(f.To))))));
            sb.Append(' ');
        }
        sb.Append("],\n");
        sb.Append("    \"acyclic\": ").Append(StrList(map.Policies.Acyclic.OrderBy(a => a, StringComparer.Ordinal).ToList())).Append('\n');
        sb.Append("  }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendRecords(StringBuilder sb, IEnumerable<string> records)
    {
        var list = records.ToList();
        if (list.Count == 0) return;
        sb.Append('\n');
        for (var i = 0; i < list.Count; i++)
        {
            sb.Append("    ").Append(list[i]);
            if (i < list.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ");
    }

    private static string Record(params (string Key, string? Value)[] fields)
    {
        var parts = fields.Where(f => f.Value is not null).Select(f => Str(f.Key) + ": " + f.Value);
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string Str(string s) => JsonSerializer.Serialize(s, StringOptions);

    private static string? OptStr(string? s) => s is null ? null : Str(s);

    private static string StrList(IReadOnlyList<string> items) =>
        "[" + string.Join(", ", items.Select(Str)) + "]";
}
