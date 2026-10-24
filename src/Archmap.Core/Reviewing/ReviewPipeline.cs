using Archmap.Analysis;
using Archmap.Bootstrap;
using Archmap.Diffing;
using Archmap.Format;
using Archmap.Model;

namespace Archmap.Reviewing;

/// <summary>
/// Describes one review: a directory inside the repository, which decides where the map and the solution are looked for,
/// and the two commits it compares. See <c>docs/review-workflow.md</c> section 4.
/// </summary>
public sealed record ReviewRequest(string Directory, string BaseCommit, string HeadCommit)
{
    /// <summary>Gets a map file on disk that is used in place of the repository's own, or <see langword="null"/>.</summary>
    public string? MapPath { get; init; }

    /// <summary>Gets the directory that the paths of <see cref="MapPath"/> are relative to. It defaults to the map's directory.</summary>
    public string? Root { get; init; }

    /// <summary>Gets the solution a review without a map analyzes, relative to <see cref="Directory"/>, or <see langword="null"/> to find it.</summary>
    public string? Solution { get; init; }

    /// <summary>Gets a value indicating whether each end is built before it is analyzed, so that source generators run.</summary>
    public bool Build { get; init; } = true;
}

/// <summary>Names a stage of a review and says what it is doing. The stages are <c>map</c>, <c>base</c>, <c>head</c> and <c>changeset</c>.</summary>
public sealed record ReviewProgress(string Stage, string Message);

/// <summary>
/// Describes a finished review for the viewer: the solution's name, the range as short hashes, the subject of the
/// head commit when the range is one commit, where the map came from, and anything the reviewer should know.
/// </summary>
/// <param name="MapKind">One of <c>repository</c>, <c>file</c> and <c>skeleton</c>.</param>
public sealed record ReviewInfo(string Solution, string Range, string? Title, string MapKind, string MapSource, IReadOnlyList<string> Warnings);

/// <summary>Holds the changeset a review draws and its description.</summary>
public sealed record ReviewResult(Changeset Changeset, ReviewInfo Info);

/// <summary>
/// Runs a review from two commits to a changeset. It chooses the map, analyzes both ends through the cache, applies the
/// map to both, and builds the changeset. See <c>docs/review-workflow.md</c> section 4.
/// </summary>
public static class ReviewPipeline
{
    /// <summary>
    /// Produces the changeset of a review. The map is the request's map file when it names one, otherwise the
    /// <c>archmap.json</c> nearest the request's directory at head or else at base, and otherwise the namespace
    /// skeleton of both ends together.
    /// </summary>
    public static async Task<ReviewResult> RunAsync(ReviewRequest request, SolutionAnalyzer analyze, ReviewCache? cache = null, IProgress<ReviewProgress>? progress = null, CancellationToken ct = default)
    {
        var directory = Path.GetFullPath(request.Directory);
        var top = Path.GetFullPath(Git.Run(directory, "rev-parse", "--show-toplevel"));
        var start = MapLocator.Relative(top, directory);
        var (baseCommit, headCommit) = (request.BaseCommit, request.HeadCommit);
        var warnings = new List<string>();
        void Report(string stage, string message) => progress?.Report(new ReviewProgress(stage, message));

        Report("map", "looking for a map");
        var chosen = ChooseMap(request, top, start, baseCommit, headCommit, warnings);
        Report("map", chosen.Source);
        var target = new AnalysisTarget(chosen.Root, chosen.Solution, request.Build);

        var sides = new[] { ("base", baseCommit), ("head", headCommit) };
        var analyses = await Task.WhenAll(sides.Select(s =>
            RevisionChangeset.AnalyzeAsync(top, target, s.Item2, analyze, cache, new StageProgress(progress, s.Item1), ct)));
        foreach (var (s, a) in sides.Zip(analyses)) Report(s.Item1, $"{a.Types.Count} types in {a.Files.Count} files");

        var (map, mapText, layoutText) = (chosen.Map, chosen.Text, chosen.LayoutText);
        if (map is null)
        {
            map = Skeleton.Build(Union(analyses[1], analyses[0]), chosen.Solution);
            mapText = MapWriter.Write(map);
            Report("map", $"derived the namespace skeleton: {map.Nodes.Count} nodes");
        }

        var info = new ReviewInfo(Path.GetFileNameWithoutExtension(chosen.Solution), $"{baseCommit[..7]}..{headCommit[..7]}",
            Title(top, baseCommit, headCommit), chosen.Kind, chosen.Source, warnings);
        var cached = cache?.ReadChangeset(baseCommit, headCommit, target, mapText!, layoutText);
        if (cached is not null)
        {
            Report("changeset", "cached");
            return new ReviewResult(cached, info);
        }

        Report("changeset", "applying the map to both ends");
        var facts = sides.Select((s, i) => RevisionChangeset.Check(map, mapText, layoutText, analyses[i], s.Item2)).ToArray();
        foreach (var w in facts[1].Warnings) warnings.Add(w);
        var root = chosen.Root == "." ? top : Path.Combine(top, chosen.Root);
        var changeset = ChangesetBuilder.Build(facts[0], facts[1], root, baseCommit, headCommit);
        cache?.WriteChangeset(baseCommit, headCommit, target, mapText!, layoutText, changeset);
        Report("changeset", $"{changeset.Files.Count(f => f.Status != "unchanged")} files and {changeset.Edges.Count(e => e.Status != "unchanged")} edges changed");
        return new ReviewResult(changeset, info);
    }

    /// <summary>
    /// Holds the map a review uses before analysis: the map and its text, or a null map when the skeleton is to be
    /// derived, the directory relative to the repository's top that its paths are relative to, and the solution
    /// relative to that directory.
    /// </summary>
    private sealed record ChosenMap(ArchMap? Map, string? Text, string? LayoutText, string Root, string Solution, string Kind, string Source);

    private static ChosenMap ChooseMap(ReviewRequest request, string top, string start, string baseCommit, string headCommit, List<string> warnings)
    {
        if (request.MapPath is not null)
        {
            var path = Path.GetFullPath(request.MapPath);
            if (!File.Exists(path)) throw new IOException($"map not found: {path}");
            var text = File.ReadAllText(path);
            var map = MapReader.Read(text);
            var mapDir = Path.GetDirectoryName(path)!;
            var root = MapLocator.Relative(top, Path.GetFullPath(request.Root ?? mapDir));
            var layoutPath = Path.Combine(mapDir, LayoutFile.FileName);
            var layout = File.Exists(layoutPath) ? File.ReadAllText(layoutPath) : null;
            return new ChosenMap(map, text, layout, root, map.Solution, "file", $"the map at {path}");
        }

        foreach (var (name, commit) in new[] { ("head", headCommit), ("base", baseCommit) })
        {
            var located = MapLocator.FindMap(top, start, commit);
            if (located is null) continue;
            var where = located.Directory == "." ? MapLocator.FileName : located.Directory + "/" + MapLocator.FileName;
            var errors = new List<string>();
            var map = MapReader.Read(located.Text, errors);
            if (map is null || errors.Count > 0)
            {
                warnings.Add($"{where} at {name} cannot be read, so the namespace skeleton is drawn instead: {string.Join("; ", errors)}");
                break;
            }
            return new ChosenMap(map, located.Text, located.LayoutText, located.Directory, map.Solution, "repository", $"{where} at {name}");
        }

        var solution = request.Solution is not null
            ? MapLocator.Relative(top, Path.GetFullPath(Path.Combine(top, start, request.Solution)))
            : MapLocator.FindSolution(top, start, headCommit);
        var source = warnings.Count > 0 ? "the namespace skeleton" : "the namespace skeleton, since the repository has no map";
        return new ChosenMap(null, null, null, MapLocator.DirectoryOf(solution), solution[(solution.LastIndexOf('/') + 1)..], "skeleton", source);
    }

    /// <summary>
    /// Returns the head analysis with the projects, types and external references that exist only at base added, so that
    /// a skeleton derived from it binds the types a changeset removes as well as those it keeps.
    /// </summary>
    private static AnalysisResult Union(AnalysisResult head, AnalysisResult baseAnalysis)
    {
        var types = head.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var projects = head.Projects.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return head with
        {
            Types = [.. head.Types, .. baseAnalysis.Types.Where(t => !types.Contains(t.Name))],
            Projects = [.. head.Projects, .. baseAnalysis.Projects.Where(p => !projects.Contains(p.Name))],
            ExternalReferences = [.. head.ExternalReferences, .. baseAnalysis.ExternalReferences],
        };
    }

    /// <summary>Returns the subject of the head commit when the range is that one commit, and <see langword="null"/> otherwise.</summary>
    private static string? Title(string top, string baseCommit, string headCommit) =>
        Git.TryRun(top, "rev-list", "--count", $"{baseCommit}..{headCommit}") == "1" ? Git.TryRun(top, "log", "-1", "--format=%s", headCommit) : null;

    /// <summary>Forwards an end's progress messages as the progress of the stage named after that end.</summary>
    private sealed class StageProgress(IProgress<ReviewProgress>? inner, string stage) : IProgress<string>
    {
        public void Report(string value) => inner?.Report(new ReviewProgress(stage, value));
    }
}
