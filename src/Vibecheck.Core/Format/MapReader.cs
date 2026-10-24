using System.Text.Json;
using Vibecheck.Model;

namespace Vibecheck.Format;

/// <summary>Thrown when a map or layout file cannot be read as a map.</summary>
public sealed class MapFormatException(IReadOnlyList<string> errors)
    : Exception("The map is not well-formed:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Reads <c>vibecheck.json</c> strictly. Every property must be one the schema names,
/// and every value must have the schema's type. Semantic rules, such as parents
/// existing, are checked by the checker rather than here.
/// </summary>
public static class MapReader
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>Parses map text, throwing <see cref="MapFormatException"/> when it is not well-formed.</summary>
    public static ArchitectureMap Read(string text)
    {
        var errors = new List<string>();
        var map = Read(text, errors);
        if (errors.Count > 0 || map is null) throw new MapFormatException(errors);
        return map;
    }

    /// <summary>Parses map text, collecting format errors instead of throwing. Returns <see langword="null"/> when the text is not JSON at all.</summary>
    public static ArchitectureMap? Read(string text, List<string> errors)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, Options);
        }
        catch (JsonException ex)
        {
            errors.Add("invalid JSON: " + ex.Message);
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("the document must be an object");
                return null;
            }
            var version = 0;
            string? solution = null;
            List<string>? exclude = null;
            var nodes = new SortedDictionary<string, NodeSpec>(StringComparer.Ordinal);
            var views = new SortedDictionary<string, ViewSpec>(StringComparer.Ordinal);
            var externals = new SortedDictionary<string, ExternalSpec>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "version":
                        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v)) version = v;
                        else errors.Add("version must be an integer");
                        break;
                    case "solution":
                        solution = ReadString(p.Value, "solution", errors);
                        break;
                    case "exclude":
                        exclude = ReadStringList(p.Value, "exclude", errors);
                        break;
                    case "nodes":
                        ReadObjectMap(p.Value, "nodes", errors, (id, el) => nodes[id] = ReadNode(id, el, errors));
                        break;
                    case "views":
                        ReadObjectMap(p.Value, "views", errors, (id, el) => views[id] = ReadView(id, el, errors));
                        break;
                    case "externals":
                        ReadObjectMap(p.Value, "externals", errors, (id, el) => externals[id] = ReadExternal(id, el, errors));
                        break;
                    default:
                        errors.Add($"unknown property \"{p.Name}\" at the top level");
                        break;
                }
            }
            if (solution is null) errors.Add("solution is required");
            if (version != 0) errors.Add($"version {version} is not supported; this tool reads version 0");
            return new ArchitectureMap
            {
                Version = version,
                Solution = solution ?? "",
                Exclude = exclude ?? [],
                Nodes = nodes,
                Views = views,
                Externals = externals,
            };
        }
    }

    private static void ReadObjectMap(JsonElement el, string path, List<string> errors, Action<string, JsonElement> each)
    {
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"{path} must be an object keyed by identifier"); return; }
        foreach (var p in el.EnumerateObject())
        {
            if (!Identifiers.IsValid(p.Name)) errors.Add($"{path}: \"{p.Name}\" is not a valid identifier");
            each(p.Name, p.Value);
        }
    }

    private static NodeSpec ReadNode(string id, JsonElement el, List<string> errors)
    {
        string? name = null, purpose = null, category = null, note = null;
        List<string>? bind = null;
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"nodes.{id} must be an object"); return new NodeSpec { Name = id }; }
        foreach (var p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": name = ReadString(p.Value, $"nodes.{id}.name", errors); break;
                case "purpose": purpose = ReadString(p.Value, $"nodes.{id}.purpose", errors); break;
                case "category":
                    category = ReadString(p.Value, $"nodes.{id}.category", errors);
                    if (category is not null && !Categories.IsKnown(category))
                        errors.Add($"nodes.{id}.category \"{category}\" is not a category; use one of {string.Join(", ", Categories.Names)}");
                    break;
                case "note": note = ReadString(p.Value, $"nodes.{id}.note", errors); break;
                case "bind": bind = ReadStringList(p.Value, $"nodes.{id}.bind", errors); break;
                default: errors.Add($"unknown property \"{p.Name}\" in nodes.{id}"); break;
            }
        }
        if (name is null) errors.Add($"nodes.{id}.name is required");
        if (bind is { Count: 0 }) errors.Add($"nodes.{id}.bind must not be empty");
        return new NodeSpec { Name = name ?? id, Purpose = purpose, Category = category, Note = note, Bind = bind };
    }

    private static ViewSpec ReadView(string id, JsonElement el, List<string> errors)
    {
        string? name = null, purpose = null, level = null;
        var groups = new SortedDictionary<string, GroupSpec>(StringComparer.Ordinal);
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"views.{id} must be an object"); return new ViewSpec { Name = id, Level = Identifiers.Root }; }
        foreach (var p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": name = ReadString(p.Value, $"views.{id}.name", errors); break;
                case "purpose": purpose = ReadString(p.Value, $"views.{id}.purpose", errors); break;
                case "level": level = ReadString(p.Value, $"views.{id}.level", errors); break;
                case "groups":
                    ReadObjectMap(p.Value, $"views.{id}.groups", errors, (gid, gel) => groups[gid] = ReadGroup(id, gid, gel, errors));
                    break;
                default: errors.Add($"unknown property \"{p.Name}\" in views.{id}"); break;
            }
        }
        if (name is null) errors.Add($"views.{id}.name is required");
        if (level is null) errors.Add($"views.{id}.level is required");
        if (groups.Count == 0) errors.Add($"views.{id}.groups must not be empty");
        return new ViewSpec { Name = name ?? id, Purpose = purpose, Level = level ?? Identifiers.Root, Groups = groups };
    }

    private static GroupSpec ReadGroup(string view, string id, JsonElement el, List<string> errors)
    {
        string? name = null, purpose = null;
        List<string>? leaves = null;
        var path = $"views.{view}.groups.{id}";
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"{path} must be an object"); return new GroupSpec { Name = id, Leaves = [] }; }
        foreach (var p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": name = ReadString(p.Value, path + ".name", errors); break;
                case "purpose": purpose = ReadString(p.Value, path + ".purpose", errors); break;
                case "leaves": leaves = ReadStringList(p.Value, path + ".leaves", errors); break;
                default: errors.Add($"unknown property \"{p.Name}\" in {path}"); break;
            }
        }
        if (name is null) errors.Add($"{path}.name is required");
        if (leaves is null or { Count: 0 }) errors.Add($"{path}.leaves is required and must not be empty");
        return new GroupSpec { Name = name ?? id, Purpose = purpose, Leaves = leaves ?? [] };
    }

    private static ExternalSpec ReadExternal(string id, JsonElement el, List<string> errors)
    {
        string? name = null, purpose = null;
        List<string>? bind = null;
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"externals.{id} must be an object"); return new ExternalSpec { Name = id, Bind = [] }; }
        foreach (var p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": name = ReadString(p.Value, $"externals.{id}.name", errors); break;
                case "purpose": purpose = ReadString(p.Value, $"externals.{id}.purpose", errors); break;
                case "bind": bind = ReadStringList(p.Value, $"externals.{id}.bind", errors); break;
                default: errors.Add($"unknown property \"{p.Name}\" in externals.{id}"); break;
            }
        }
        if (name is null) errors.Add($"externals.{id}.name is required");
        if (bind is null or { Count: 0 }) errors.Add($"externals.{id}.bind is required and must not be empty");
        return new ExternalSpec { Name = name ?? id, Purpose = purpose, Bind = bind ?? [] };
    }

    private static string? ReadString(JsonElement el, string path, List<string> errors)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        errors.Add($"{path} must be a string");
        return null;
    }

    private static List<string>? ReadStringList(JsonElement el, string path, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Array) { errors.Add($"{path} must be an array of strings"); return null; }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            else errors.Add($"{path} must contain only strings");
        }
        return list;
    }
}
