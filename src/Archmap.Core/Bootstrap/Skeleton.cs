using Archmap.Analysis;
using Archmap.Model;

namespace Archmap.Bootstrap;

/// <summary>
/// Builds the deterministic skeleton map of <c>docs/archmap-spec.md</c> section 5.1:
/// one top-level node per project and one node per directory that contains compiled
/// files, every level observed, no edges declared.
/// </summary>
public static class Skeleton
{
    public static ArchMap Build(AnalysisResult analysis, string solutionRelativePath)
    {
        var map = new ArchMap { Version = 0, Solution = solutionRelativePath.Replace('\\', '/') };
        var prefix = CommonPrefixSegments(analysis.Projects.Select(p => p.Name).ToList());
        var topLevelIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in analysis.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (project.Files.Count == 0) continue;
            var stripped = string.Join('.', project.Name.Split('.').Skip(prefix));
            var id = Unique(Identifiers.NormalizeSegment(stripped), topLevelIds);
            var root = new Dir();
            var linked = new List<string>();
            var dirPrefix = project.Directory.Length == 0 ? "" : project.Directory.Replace('\\', '/') + "/";
            foreach (var file in project.Files)
            {
                if (dirPrefix.Length > 0 && !file.StartsWith(dirPrefix, StringComparison.Ordinal)) { linked.Add(file); continue; }
                var rel = file[dirPrefix.Length..];
                var segments = rel.Split('/');
                var dir = root;
                foreach (var segment in segments.Take(segments.Length - 1)) dir = dir.Child(segment);
                dir.Files.Add(file);
            }
            Emit(map, root, id, project.Name, project.Directory.Replace('\\', '/'), forceContainer: linked.Count > 0);
            if (linked.Count > 0)
            {
                map.Nodes[id + ".linked"] = new NodeSpec { Name = "linked", Purpose = "Files compiled from outside the project directory.", Bind = linked };
            }
        }
        AddExternals(map, analysis);
        return map;
    }

    /// <summary>The assembly name patterns that the skeleton binds to one <c>framework</c> external.</summary>
    internal static readonly string[] FrameworkPatterns = ["System.*", "Microsoft.Win32.*", "Microsoft.VisualBasic*", "netstandard", "mscorlib", "WindowsBase"];

    /// <summary>
    /// Binds every referenced assembly so that the root externals policy can be set to
    /// declared at once: framework assemblies under one external, and one external per
    /// other assembly, named after it.
    /// </summary>
    private static void AddExternals(ArchMap map, AnalysisResult analysis)
    {
        var assemblies = analysis.ExternalReferences.Select(e => e.Assembly).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (assemblies.Count == 0) return;
        var framework = assemblies.Where(IsFramework).ToList();
        if (framework.Count > 0)
        {
            map.Externals["framework"] = new ExternalSpec { Name = "Framework", Purpose = "Framework assemblies other than the core library.", Bind = FrameworkPatterns };
        }
        var used = new HashSet<string>(StringComparer.Ordinal) { "framework" };
        foreach (var assembly in assemblies.Where(a => !IsFramework(a)))
        {
            var id = Unique(Identifiers.NormalizeSegment(assembly), used);
            map.Externals[id] = new ExternalSpec { Name = assembly, Bind = [assembly] };
        }
    }

    private static bool IsFramework(string assembly) =>
        FrameworkPatterns.Any(p => p.EndsWith('*') ? assembly.StartsWith(p[..^1], StringComparison.Ordinal) : assembly == p);

    private static void Emit(ArchMap map, Dir dir, string id, string name, string path, bool forceContainer = false)
    {
        if (dir.Children.Count == 0 && !forceContainer)
        {
            map.Nodes[id] = new NodeSpec { Name = name, Bind = [Glob(path, recursive: true)] };
            return;
        }
        map.Nodes[id] = new NodeSpec { Name = name };
        if (dir.Files.Count > 0)
        {
            map.Nodes[id + ".self"] = new NodeSpec { Name = "self", Bind = [Glob(path, recursive: false)] };
        }
        var used = new HashSet<string>(StringComparer.Ordinal) { "self", "linked" };
        foreach (var (segment, child) in dir.Children.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var childId = Unique(Identifiers.NormalizeSegment(segment), used);
            Emit(map, child, id + "." + childId, segment, path.Length == 0 ? segment : path + "/" + segment);
        }
    }

    private static string Glob(string dir, bool recursive) =>
        (dir.Length == 0 ? "" : dir + "/") + (recursive ? "**/*.cs" : "*.cs");

    private static string Unique(string id, HashSet<string> used)
    {
        var candidate = id;
        for (var n = 2; !used.Add(candidate); n++) candidate = id + "-" + n;
        return candidate;
    }

    /// <summary>Returns how many leading dot-separated segments every project name shares, leaving each name at least one segment.</summary>
    internal static int CommonPrefixSegments(IReadOnlyList<string> names)
    {
        if (names.Count < 2) return 0;
        var split = names.Select(n => n.Split('.')).ToList();
        var n = 0;
        while (split.All(s => s.Length > n + 1 && s[n] == split[0][n])) n++;
        return n;
    }

    private sealed class Dir
    {
        public SortedDictionary<string, Dir> Children { get; } = new(StringComparer.Ordinal);
        public List<string> Files { get; } = [];

        public Dir Child(string segment)
        {
            if (!Children.TryGetValue(segment, out var d)) Children[segment] = d = new Dir();
            return d;
        }
    }
}
