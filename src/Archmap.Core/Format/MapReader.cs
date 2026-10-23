using System.Text.Json;
using Archmap.Model;

namespace Archmap.Format;

/// <summary>Thrown when a map or layout file cannot be read as a map.</summary>
public sealed class MapFormatException(IReadOnlyList<string> errors)
    : Exception("The map is not well-formed:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Reads <c>archmap.json</c> strictly. Every property must be one the schema names,
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
    public static ArchMap Read(string text)
    {
        var errors = new List<string>();
        var map = Read(text, errors);
        if (errors.Count > 0 || map is null) throw new MapFormatException(errors);
        return map;
    }

    /// <summary>Parses map text, collecting format errors instead of throwing. Returns <see langword="null"/> when the text is not JSON at all.</summary>
    public static ArchMap? Read(string text, List<string> errors)
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
            var externals = new SortedDictionary<string, ExternalSpec>(StringComparer.Ordinal);
            var edges = new List<EdgeSpec>();
            var policies = new PolicySpec();
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
                    case "externals":
                        ReadObjectMap(p.Value, "externals", errors, (id, el) => externals[id] = ReadExternal(id, el, errors));
                        break;
                    case "edges":
                        if (p.Value.ValueKind != JsonValueKind.Array) { errors.Add("edges must be an array"); break; }
                        var i = 0;
                        foreach (var el in p.Value.EnumerateArray()) edges.Add(ReadEdge(el, $"edges[{i++}]", errors));
                        break;
                    case "policies":
                        policies = ReadPolicies(p.Value, errors);
                        break;
                    default:
                        errors.Add($"unknown property \"{p.Name}\" at the top level");
                        break;
                }
            }
            if (solution is null) errors.Add("solution is required");
            if (version != 0) errors.Add($"version {version} is not supported; this tool reads version 0");
            return new ArchMap
            {
                Version = version,
                Solution = solution ?? "",
                Exclude = exclude ?? [],
                Nodes = nodes,
                Externals = externals,
                Edges = edges,
                Policies = policies,
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
        string? name = null, purpose = null, note = null, policy = null;
        List<string>? bind = null;
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"nodes.{id} must be an object"); return new NodeSpec { Name = id }; }
        foreach (var p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": name = ReadString(p.Value, $"nodes.{id}.name", errors); break;
                case "purpose": purpose = ReadString(p.Value, $"nodes.{id}.purpose", errors); break;
                case "note": note = ReadString(p.Value, $"nodes.{id}.note", errors); break;
                case "edges":
                    policy = ReadString(p.Value, $"nodes.{id}.edges", errors);
                    if (policy is not (ArchMap.Declared or ArchMap.Observed)) errors.Add($"nodes.{id}.edges must be \"declared\" or \"observed\"");
                    break;
                case "bind": bind = ReadStringList(p.Value, $"nodes.{id}.bind", errors); break;
                default: errors.Add($"unknown property \"{p.Name}\" in nodes.{id}"); break;
            }
        }
        if (name is null) errors.Add($"nodes.{id}.name is required");
        if (bind is not null && policy is not null) errors.Add($"nodes.{id} is a leaf and cannot carry an edges policy");
        if (bind is { Count: 0 }) errors.Add($"nodes.{id}.bind must not be empty");
        return new NodeSpec { Name = name ?? id, Purpose = purpose, Note = note, Edges = policy, Bind = bind };
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

    private static EdgeSpec ReadEdge(JsonElement el, string path, List<string> errors)
    {
        string? from = null, to = null, label = null, payload = null;
        var asserted = false;
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"{path} must be an object"); return new EdgeSpec { From = "", To = "" }; }
        foreach (var p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "from": from = ReadString(p.Value, path + ".from", errors); break;
                case "to": to = ReadString(p.Value, path + ".to", errors); break;
                case "label": label = ReadString(p.Value, path + ".label", errors); break;
                case "payload": payload = ReadString(p.Value, path + ".payload", errors); break;
                case "asserted":
                    if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) asserted = p.Value.GetBoolean();
                    else errors.Add($"{path}.asserted must be a boolean");
                    break;
                default: errors.Add($"unknown property \"{p.Name}\" in {path}"); break;
            }
        }
        if (from is null) errors.Add($"{path}.from is required");
        if (to is null) errors.Add($"{path}.to is required");
        return new EdgeSpec { From = from ?? "", To = to ?? "", Label = label, Payload = payload, Asserted = asserted };
    }

    private static PolicySpec ReadPolicies(JsonElement el, List<string> errors)
    {
        var root = new RootPolicy();
        var forbid = new List<ForbidSpec>();
        var acyclic = new List<string>();
        if (el.ValueKind != JsonValueKind.Object) { errors.Add("policies must be an object"); return new PolicySpec(); }
        foreach (var p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "root":
                    if (p.Value.ValueKind != JsonValueKind.Object) { errors.Add("policies.root must be an object"); break; }
                    string edges = ArchMap.Observed, externals = ArchMap.Observed;
                    foreach (var q in p.Value.EnumerateObject())
                    {
                        switch (q.Name)
                        {
                            case "edges": edges = ReadString(q.Value, "policies.root.edges", errors) ?? edges; break;
                            case "externals": externals = ReadString(q.Value, "policies.root.externals", errors) ?? externals; break;
                            default: errors.Add($"unknown property \"{q.Name}\" in policies.root"); break;
                        }
                    }
                    if (edges is not (ArchMap.Declared or ArchMap.Observed)) errors.Add("policies.root.edges must be \"declared\" or \"observed\"");
                    if (externals is not (ArchMap.Declared or ArchMap.Observed)) errors.Add("policies.root.externals must be \"declared\" or \"observed\"");
                    root = new RootPolicy { Edges = edges, Externals = externals };
                    break;
                case "forbid":
                    if (p.Value.ValueKind != JsonValueKind.Array) { errors.Add("policies.forbid must be an array"); break; }
                    var i = 0;
                    foreach (var f in p.Value.EnumerateArray())
                    {
                        var path = $"policies.forbid[{i++}]";
                        string? from = null, to = null;
                        if (f.ValueKind != JsonValueKind.Object) { errors.Add($"{path} must be an object"); continue; }
                        foreach (var q in f.EnumerateObject())
                        {
                            switch (q.Name)
                            {
                                case "from": from = ReadString(q.Value, path + ".from", errors); break;
                                case "to": to = ReadString(q.Value, path + ".to", errors); break;
                                default: errors.Add($"unknown property \"{q.Name}\" in {path}"); break;
                            }
                        }
                        if (from is null || to is null) errors.Add($"{path} needs from and to");
                        forbid.Add(new ForbidSpec { From = from ?? "", To = to ?? "" });
                    }
                    break;
                case "acyclic":
                    acyclic = ReadStringList(p.Value, "policies.acyclic", errors) ?? [];
                    break;
                default:
                    errors.Add($"unknown property \"{p.Name}\" in policies");
                    break;
            }
        }
        return new PolicySpec { Root = root, Forbid = forbid, Acyclic = acyclic };
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
