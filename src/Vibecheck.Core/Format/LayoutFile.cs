using System.Text;
using System.Text.Json;
using Vibecheck.Model;

namespace Vibecheck.Format;

/// <summary>
/// The stored node positions per view, from <c>vibecheck.layout.json</c>. The layout is
/// presentation only and never affects conformance. Each view is keyed by the
/// container it shows and maps each child to <c>[x, y, w, h]</c>.
/// </summary>
public sealed class LayoutFile
{
    public const string FileName = "vibecheck.layout.json";

    public int Version { get; init; }

    public SortedDictionary<string, SortedDictionary<string, double[]>> Views { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Parses layout text, collecting shape errors. Returns <see langword="null"/> when the text is not JSON.</summary>
    public static LayoutFile? Read(string text, List<string> errors)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            errors.Add("layout: invalid JSON: " + ex.Message);
            return null;
        }
        using (doc)
        {
            var layout = new LayoutFile();
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { errors.Add("layout: the document must be an object"); return null; }
            var version = 0;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "version":
                        if (!(p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out version))) errors.Add("layout: version must be an integer");
                        break;
                    case "views":
                        if (p.Value.ValueKind != JsonValueKind.Object) { errors.Add("layout: views must be an object"); break; }
                        foreach (var view in p.Value.EnumerateObject())
                        {
                            var entries = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
                            if (view.Value.ValueKind != JsonValueKind.Object) { errors.Add($"layout: views.{view.Name} must be an object"); continue; }
                            foreach (var child in view.Value.EnumerateObject())
                            {
                                if (child.Value.ValueKind != JsonValueKind.Array || child.Value.GetArrayLength() != 4
                                    || child.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number))
                                {
                                    errors.Add($"layout: views.{view.Name}.{child.Name} must be [x, y, w, h]");
                                    continue;
                                }
                                entries[child.Name] = child.Value.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                            }
                            layout.Views[view.Name] = entries;
                        }
                        break;
                    default:
                        errors.Add($"layout: unknown property \"{p.Name}\"");
                        break;
                }
            }
            if (version != 0) errors.Add($"layout: version {version} is not supported");
            return layout;
        }
    }

    /// <summary>Returns the identifiers the layout mentions that the map does not define. These are warnings, not failures.</summary>
    public IEnumerable<string> UnknownIdentifiers(ArchitectureMap map)
    {
        foreach (var (view, entries) in Views)
        {
            if (!map.IsContainer(view)) yield return view;
            foreach (var child in entries.Keys)
            {
                if (!map.Exists(child)) yield return child;
            }
        }
    }

    /// <summary>Returns the canonical text of the layout.</summary>
    public string Write()
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"version\": ").Append(Version).Append(",\n  \"views\": {");
        var views = Views.OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
        if (views.Count > 0)
        {
            sb.Append('\n');
            for (var i = 0; i < views.Count; i++)
            {
                sb.Append("    ").Append(JsonSerializer.Serialize(views[i].Key)).Append(": {");
                var entries = views[i].Value.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
                if (entries.Count > 0)
                {
                    sb.Append('\n');
                    for (var j = 0; j < entries.Count; j++)
                    {
                        sb.Append("      ").Append(JsonSerializer.Serialize(entries[j].Key)).Append(": [")
                          .Append(string.Join(", ", entries[j].Value.Select(d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture))))
                          .Append(']');
                        if (j < entries.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append("    ");
                }
                sb.Append('}');
                if (i < views.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ");
        }
        sb.Append("}\n}\n");
        return sb.ToString();
    }
}
