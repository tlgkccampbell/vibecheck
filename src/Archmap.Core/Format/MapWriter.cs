using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Archmap.Model;

namespace Archmap.Format;

/// <summary>
/// Writes <c>archmap.json</c> in canonical form. Each node, external and group entry
/// occupies one line, the exclude list occupies one line in the order written,
/// nodes, views, groups and externals are sorted by identifier, and keys appear in
/// the schema's order. The output uses two-space indentation, LF line endings and
/// ends with a newline.
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
        sb.Append("  \"exclude\": ").Append(StrList(map.Exclude)).Append(",\n");

        sb.Append("  \"nodes\": {");
        AppendRecords(sb, "    ", map.Nodes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            Str(kv.Key) + ": " + Record(
                ("name", Str(kv.Value.Name)),
                ("purpose", OptStr(kv.Value.Purpose)),
                ("note", OptStr(kv.Value.Note)),
                ("bind", kv.Value.Bind is null ? null : StrList(kv.Value.Bind)))));
        sb.Append("},\n");

        sb.Append("  \"views\": {");
        var views = map.Views.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (views.Count > 0)
        {
            sb.Append('\n');
            for (var i = 0; i < views.Count; i++)
            {
                var (id, view) = views[i];
                sb.Append("    ").Append(Str(id)).Append(": { ");
                sb.Append(string.Join(", ", new[] { ("name", Str(view.Name)), ("purpose", OptStr(view.Purpose)), ("level", Str(view.Level)) }
                    .Where(f => f.Item2 is not null).Select(f => Str(f.Item1) + ": " + f.Item2)));
                sb.Append(", \"groups\": {");
                AppendRecords(sb, "      ", view.Groups.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                    Str(kv.Key) + ": " + Record(
                        ("name", Str(kv.Value.Name)),
                        ("purpose", OptStr(kv.Value.Purpose)),
                        ("leaves", StrList(kv.Value.Leaves)))), "    ");
                sb.Append("} }");
                if (i < views.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ");
        }
        sb.Append("},\n");

        sb.Append("  \"externals\": {");
        AppendRecords(sb, "    ", map.Externals.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            Str(kv.Key) + ": " + Record(
                ("name", Str(kv.Value.Name)),
                ("purpose", OptStr(kv.Value.Purpose)),
                ("bind", StrList(kv.Value.Bind)))));
        sb.Append("}\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendRecords(StringBuilder sb, string indent, IEnumerable<string> records, string closingIndent = "  ")
    {
        var list = records.ToList();
        if (list.Count == 0) return;
        sb.Append('\n');
        for (var i = 0; i < list.Count; i++)
        {
            sb.Append(indent).Append(list[i]);
            if (i < list.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append(closingIndent);
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
