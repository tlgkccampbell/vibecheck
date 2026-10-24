using Vibecheck.Analysis;
using Vibecheck.Model;

namespace Vibecheck.Bootstrap;

/// <summary>
/// Builds the deterministic skeleton map of <c>docs/vibecheck-spec.md</c> section 5.1:
/// one top-level node per project and one node per namespace segment beyond the
/// project's root namespace, which leaves the unsorted leaf empty by construction.
/// Types in <c>System</c> namespaces and every project that references a test
/// framework are excluded before any node is derived.
/// </summary>
public static class Skeleton
{
    public static ArchitectureMap Build(AnalysisResult analysis, string solutionRelativePath)
    {
        var exclude = ExcludePatterns(analysis);
        var map = new ArchitectureMap { Version = 0, Solution = solutionRelativePath.Replace('\\', '/'), Exclude = exclude };
        (analysis, _) = Exclusion.Parse(exclude, []).Apply(analysis);
        var prefix = CommonPrefixSegments(analysis.Projects.Select(p => p.Name).ToList());
        var topLevelIds = new HashSet<string>(StringComparer.Ordinal);
        var typesByAssembly = analysis.Types.GroupBy(t => t.Assembly, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var project in analysis.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!typesByAssembly.TryGetValue(project.AssemblyName, out var types) || types.Count == 0) continue;
            var stripped = string.Join('.', project.Name.Split('.').Skip(prefix));
            var id = Unique(Identifiers.NormalizeSegment(stripped), topLevelIds);
            // A pattern is qualified when another assembly declares types it would otherwise
            // match: the namespace itself for a `*` pattern, the namespace or any beneath it for `**`.
            bool Shared(string ns, bool recursive) => analysis.Types.Any(t => t.Assembly != project.AssemblyName
                && (t.Namespace == ns || (recursive && t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal))));
            string Qualify(string pattern, string ns, bool recursive) => Shared(ns, recursive) ? project.AssemblyName + "::" + pattern : pattern;

            var namespaced = types.Where(t => t.Namespace.Length > 0).ToList();
            var global = types.Where(t => t.Namespace.Length == 0).ToList();
            // The root namespace comes from hand-written types. A generator that emits into
            // its own namespace, as the regex generator does, must not pull the root up to nothing.
            var handWritten = namespaced.Where(t => t.Generator is null).Select(t => t.Namespace).ToList();
            var rootNs = RootNamespace(handWritten.Count > 0 ? handWritten : namespaced.Select(t => t.Namespace).ToList());
            var tree = new Ns();
            foreach (var t in namespaced)
            {
                var under = rootNs.Length > 0 && (t.Namespace == rootNs || t.Namespace.StartsWith(rootNs + ".", StringComparison.Ordinal));
                var rest = !under ? t.Namespace : t.Namespace == rootNs ? "" : t.Namespace[(rootNs.Length + 1)..];
                var node = tree;
                if (!under) node = node.Child("");
                foreach (var segment in rest.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node.Child(segment);
                node.Types++;
            }

            if (global.Count > 0 && namespaced.Count == 0)
            {
                map.Nodes[id] = new NodeSpec { Name = project.Name, Bind = [project.AssemblyName + "::*"] };
                continue;
            }
            Emit(map, tree, id, project.Name, rootNs, Qualify, forceContainer: global.Count > 0);
            if (global.Count > 0)
            {
                map.Nodes[id + ".global"] = new NodeSpec { Name = "global", Purpose = "Types in the global namespace.", Bind = [project.AssemblyName + "::*"] };
            }
        }
        AddExternals(map, analysis);
        return map;
    }

    /// <summary>
    /// Emits the node for a namespace: a leaf when it has no child namespaces, else a
    /// container with a <c>self</c> leaf for its own types. A child keyed by the empty
    /// string holds namespaces outside the project's root namespace, which are emitted
    /// from the top of the namespace hierarchy under the same node.
    /// </summary>
    private static void Emit(ArchitectureMap map, Ns ns, string id, string name, string fullNs, Func<string, string, bool, string> qualify, bool forceContainer = false)
    {
        if (ns.Children.Count == 0 && !forceContainer)
        {
            map.Nodes[id] = new NodeSpec { Name = name, Bind = [qualify(Pattern(fullNs, recursive: true), fullNs, true)] };
            return;
        }
        map.Nodes[id] = new NodeSpec { Name = name };
        if (ns.Types > 0)
        {
            map.Nodes[id + ".self"] = new NodeSpec { Name = "self", Bind = [qualify(Pattern(fullNs, recursive: false), fullNs, false)] };
        }
        var used = new HashSet<string>(StringComparer.Ordinal) { "self", "global" };
        foreach (var (segment, child) in ns.Children.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (segment.Length == 0)
            {
                foreach (var (outsideSegment, outside) in child.Children.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var outsideId = Unique(Identifiers.NormalizeSegment(outsideSegment), used);
                    Emit(map, outside, id + "." + outsideId, outsideSegment, outsideSegment, qualify);
                }
                continue;
            }
            var childId = Unique(Identifiers.NormalizeSegment(segment), used);
            Emit(map, child, id + "." + childId, segment, fullNs.Length == 0 ? segment : fullNs + "." + segment, qualify);
        }
    }

    private static string Pattern(string ns, bool recursive) =>
        (ns.Length == 0 ? "" : ns + ".") + (recursive ? "**" : "*");

    /// <summary>Returns the longest namespace prefix, in whole segments, shared by every namespace in the list.</summary>
    internal static string RootNamespace(IReadOnlyList<string> namespaces)
    {
        if (namespaces.Count == 0) return "";
        var split = namespaces.Select(n => n.Split('.')).ToList();
        var n = 0;
        while (split.All(s => s.Length > n && s[n] == split[0][n])) n++;
        return string.Join('.', split[0].Take(n));
    }

    /// <summary>The assembly name patterns by which the skeleton recognizes a test project: a project whose types reference any of them.</summary>
    internal static readonly string[] TestFrameworkPatterns = ["xunit*", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework", "TUnit.Core"];

    /// <summary>
    /// Returns the exclude list the skeleton writes: <c>System.**</c> for generator
    /// output and polyfills that land in the framework's namespaces, and every type
    /// of each project that references a test framework, qualified by its assembly.
    /// </summary>
    internal static List<string> ExcludePatterns(AnalysisResult analysis)
    {
        var assemblyOf = analysis.Types.ToDictionary(t => t.Name, t => t.Assembly, StringComparer.Ordinal);
        var testAssemblies = analysis.ExternalReferences
            .Where(e => TestFrameworkPatterns.Any(p => AssemblyMatches(p, e.Assembly)) && assemblyOf.ContainsKey(e.FromType))
            .Select(e => assemblyOf[e.FromType])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal);
        return ["System.**", .. testAssemblies.Select(a => a + "::**")];
    }

    /// <summary>The assembly name patterns that the skeleton binds to one <c>framework</c> external.</summary>
    internal static readonly string[] FrameworkPatterns = ["System.*", "Microsoft.Win32.*", "Microsoft.VisualBasic*", "netstandard", "mscorlib", "WindowsBase"];

    /// <summary>
    /// Binds every referenced assembly so that no port is unbound: framework assemblies
    /// under one external, and one external per other assembly, named after it.
    /// </summary>
    private static void AddExternals(ArchitectureMap map, AnalysisResult analysis)
    {
        var assemblies = analysis.ExternalReferences.Select(e => e.Assembly).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (assemblies.Count == 0) return;
        if (assemblies.Any(IsFramework))
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

    private static bool IsFramework(string assembly) => FrameworkPatterns.Any(p => AssemblyMatches(p, assembly));

    /// <summary>Matches an assembly name against a pattern whose only wildcard is a trailing <c>*</c>.</summary>
    private static bool AssemblyMatches(string pattern, string assembly) =>
        pattern.EndsWith('*') ? assembly.StartsWith(pattern[..^1], StringComparison.Ordinal) : assembly == pattern;

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

    private sealed class Ns
    {
        public SortedDictionary<string, Ns> Children { get; } = new(StringComparer.Ordinal);
        public int Types { get; set; }

        public Ns Child(string segment)
        {
            if (!Children.TryGetValue(segment, out var d)) Children[segment] = d = new Ns();
            return d;
        }
    }
}
