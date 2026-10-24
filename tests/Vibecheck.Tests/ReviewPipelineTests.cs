using System.Collections.Concurrent;
using Vibecheck.Analysis;
using Vibecheck.Diffing;
using Vibecheck.Format;
using Vibecheck.Model;
using Vibecheck.Reviewing;
using Xunit;

namespace Vibecheck.Tests;

public class ReviewPipelineTests
{
    private const string Gone = "A.X.Gone";
    private const string GoneFile = "src/A/X/Gone.cs";

    /// <summary>
    /// Returns an analyzer that stands in for Roslyn and records each root it analyzes. It reports the synthetic analysis
    /// with each file's hash taken from its text in the checkout, adds a call from Thing to Service when X1.cs mentions
    /// Service, and adds the type Gone when its file exists.
    /// </summary>
    internal static SolutionAnalyzer Fake(ConcurrentBag<string> roots) => (root, solution, progress, ct) =>
    {
        roots.Add(root);
        var x1 = File.ReadAllText(Path.Combine(root, TestData.X1));
        var extra = x1.Contains("Service") ? new[] { new RawDependency(TestData.Thing, TestData.Service, "calls", "B.Service.Run", TestData.X1, 2) } : [];
        var gone = File.Exists(Path.Combine(root, GoneFile));
        var analysis = TestData.Analysis(extra,
            extraTypes: gone ? [new TypeInfo(Gone, "Example.A", [GoneFile])] : null,
            extraFiles: gone ? [new SourceFile(GoneFile, "g", "A")] : null);
        return Task.FromResult(analysis with
        {
            Files = analysis.Files.Select(f => f with { Hash = File.ReadAllText(Path.Combine(root, f.Path)).GetHashCode().ToString() }).ToList(),
        });
    };

    /// <summary>Writes the synthetic solution's files under <c>app/</c>, with X1.cs holding the text given.</summary>
    internal static void WriteApp(TempRepo repo, string x1)
    {
        repo.Write("app/Example.sln", "");
        foreach (var path in new[] { TestData.X2, TestData.Y1, TestData.B1, TestData.S1 }) repo.Write("app/" + path, "class C { }\n");
        repo.Write("app/" + TestData.X1, x1);
    }

    /// <summary>Commits the synthetic solution twice, the second time with a call from Thing to Service, and returns both commits.</summary>
    private static (string Base, string Head) TwoCommits(TempRepo repo, Action? beforeFirst = null)
    {
        WriteApp(repo, "class Thing { }\n");
        beforeFirst?.Invoke();
        var before = repo.Commit("one");
        WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var after = repo.Commit("call the service");
        return (before, after);
    }

    private static ReviewRequest Request(TempRepo repo, string dir, string baseCommit, string headCommit) =>
        new(Path.Combine(repo.Path, dir), baseCommit, headCommit) { Build = false };

    [Fact]
    public async Task PairsRenamesAsTheRequestPairsThemWithPathsFromTheRepositorysTop()
    {
        using var repo = new TempRepo();
        var (before, _) = TwoCommits(repo, () =>
        {
            repo.Write("app/vibecheck.json", MapWriter.Write(TestData.Map()));
            repo.Write("app/docs/old.md", "Something entirely different.\n");
        });
        File.Delete(Path.Combine(repo.Path, "app/docs/old.md"));
        repo.Write("app/docs/new.md", "A wholly rewritten page.\n");
        var after = repo.Commit("rewrite the page");

        var request = Request(repo, "app", before, after) with { Renames = new Dictionary<string, string> { ["app/docs/new.md"] = "app/docs/old.md" } };
        var result = await ReviewPipeline.RunAsync(request, Fake([]));

        var page = result.Changeset.Files.Single(f => f.Path == "docs/new.md");
        Assert.Equal(("renamed", "docs/old.md"), (page.Status, page.PreviousPath));
        Assert.DoesNotContain(result.Changeset.Files, f => f.Path == "docs/old.md");
        // Without the request's pairs, git finds the two too different to be a rename.
        var plain = await ReviewPipeline.RunAsync(Request(repo, "app", before, after), Fake([]));
        Assert.Equal("added", plain.Changeset.Files.Single(f => f.Path == "docs/new.md").Status);
    }

    [Fact]
    public async Task UsesTheMapNearestTheStartDirectoryAtHead()
    {
        using var repo = new TempRepo();
        var (before, after) = TwoCommits(repo, () => repo.Write("app/vibecheck.json", MapWriter.Write(TestData.Map())));

        var result = await ReviewPipeline.RunAsync(Request(repo, "app/src/A", before, after), Fake([]));

        Assert.Equal(("repository", "app/vibecheck.json at head"), (result.Info.MapKind, result.Info.MapSource));
        Assert.Equal(("Example", "call the service"), (result.Info.Solution, result.Info.Title));
        Assert.Equal("added", result.Changeset.Edges.Single(e => e.From == "a.x" && e.To == "b").Status);
        Assert.Equal([TestData.X1], result.Changeset.Hunks.Select(h => h.Path));
    }

    [Fact]
    public async Task ReadsTheMapAndTheDiffAsUtf8()
    {
        using var repo = new TempRepo();
        var map = TestData.Map();
        map.Nodes["b"] = map.Nodes["b"] with { Purpose = "Café façade — naïve." };
        WriteApp(repo, "class Thing { }\n");
        repo.Write("app/vibecheck.json", MapWriter.Write(map));
        var before = repo.Commit("one");
        WriteApp(repo, "class Thing { string Name => \"Größe\"; }\n");
        var after = repo.Commit("two");

        var result = await ReviewPipeline.RunAsync(Request(repo, "app", before, after), Fake([]));

        Assert.Equal("Café façade — naïve.", result.Changeset.Map.Nodes["b"].Purpose);
        Assert.Empty(result.Info.Warnings);
        Assert.Contains(result.Changeset.Hunks.Single().Hunks.SelectMany(h => h.Lines), l => l[0] == "+" && l[1].Contains("Größe"));
    }

    [Fact]
    public async Task DerivesTheSkeletonFromBothEndsWhenTheRepositoryHasNoMap()
    {
        using var repo = new TempRepo();
        WriteApp(repo, "class Thing { }\n");
        repo.Write("app/" + GoneFile, "class Gone { }\n");
        var before = repo.Commit("one");
        File.Delete(Path.Combine(repo.Path, "app", GoneFile));
        var after = repo.Commit("remove Gone");

        var result = await ReviewPipeline.RunAsync(Request(repo, ".", before, after), Fake([]));

        Assert.Equal("skeleton", result.Info.MapKind);
        var gone = result.Changeset.Types.Single(t => t.Name == Gone);
        Assert.Equal("removed", gone.Status);
        Assert.NotEqual(ArchitectureMap.Unsorted, gone.LeafBase);
    }

    [Fact]
    public async Task DrawsTheSkeletonWithAWarningWhenTheMapCannotBeRead()
    {
        using var repo = new TempRepo();
        var (before, after) = TwoCommits(repo, () => repo.Write("app/vibecheck.json", "{ \"version\": 0, \"nodes\": { \"a\": { \"declare\": [] } } }\n"));

        var result = await ReviewPipeline.RunAsync(Request(repo, "app", before, after), Fake([]));

        Assert.Equal("skeleton", result.Info.MapKind);
        Assert.Contains(result.Info.Warnings, w => w.StartsWith("app/vibecheck.json at head cannot be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadsBothEndsFromTheCacheTheSecondTimeAndAfterTheMapChanges()
    {
        using var repo = new TempRepo();
        using var temp = new TempDirectory();
        var (before, after) = TwoCommits(repo, () => repo.Write("app/vibecheck.json", MapWriter.Write(TestData.Map())));
        var cache = new ReviewCache(temp.Path, Path.Combine(temp.Path, "repo"));
        var roots = new ConcurrentBag<string>();

        var first = await ReviewPipeline.RunAsync(Request(repo, "app", before, after), Fake(roots), cache);
        Assert.Equal(2, roots.Count);

        var stages = new List<ReviewProgress>();
        var second = await ReviewPipeline.RunAsync(Request(repo, "app", before, after), Fake(roots), cache, new Recorder(stages));
        Assert.Equal(2, roots.Count);
        Assert.Contains(new ReviewProgress("changeset", "cached"), stages);
        Assert.Equal(FactsJson(first.Changeset), FactsJson(second.Changeset));

        var other = TestData.Map();
        other.Nodes.Remove("a.y");
        other.Nodes["a.x"] = other.Nodes["a.x"] with { Bind = ["A.**"] };
        var mapPath = Path.Combine(temp.Path, "other", "vibecheck.json");
        Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        File.WriteAllText(mapPath, MapWriter.Write(other));
        var third = await ReviewPipeline.RunAsync(Request(repo, "app", before, after) with { MapPath = mapPath, Root = Path.Combine(repo.Path, "app") }, Fake(roots), cache);
        Assert.Equal(2, roots.Count);
        Assert.Equal("file", third.Info.MapKind);
        Assert.DoesNotContain(third.Changeset.Types, t => t.LeafHead == "a.y");
    }

    [Fact]
    public async Task SharesOneAnalysisBetweenConcurrentRequestsAndDoesNotStoreAFailure()
    {
        using var temp = new TempDirectory();
        var cache = new ReviewCache(temp.Path, Path.Combine(temp.Path, "repo"));
        var target = new AnalysisTarget(".", "Example.sln", false);
        var calls = 0;
        var gate = new TaskCompletionSource<AnalysisResult>();
        Task<AnalysisResult> Produce() { Interlocked.Increment(ref calls); return gate.Task; }

        var a = cache.AnalysisAsync(target, new string('a', 40), Produce);
        var b = cache.AnalysisAsync(target, new string('a', 40), Produce);
        gate.SetResult(TestData.Analysis());
        await Task.WhenAll(a, b);
        Assert.Equal(1, calls);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.AnalysisAsync(target, new string('b', 40), () => throw new InvalidOperationException("broken")));
        var again = await cache.AnalysisAsync(target, new string('b', 40), () => Task.FromResult(TestData.Analysis()));
        Assert.Equal(TestData.Analysis().Types.Count, again.Types.Count);
    }

    [Fact]
    public async Task EvictsEntriesOverTheLimit()
    {
        using var temp = new TempDirectory();
        var cache = new ReviewCache(temp.Path, Path.Combine(temp.Path, "repo"), limit: 1);
        var target = new AnalysisTarget(".", "Example.sln", false);
        await cache.AnalysisAsync(target, new string('a', 40), () => Task.FromResult(TestData.Analysis()));
        var calls = 0;
        await cache.AnalysisAsync(target, new string('a', 40), () => { calls++; return Task.FromResult(TestData.Analysis()); });
        Assert.Equal(1, calls);
    }

    private static string FactsJson(Changeset changeset) => Checking.FactsJson.Serialize(changeset);

    private sealed class Recorder(List<ReviewProgress> list) : IProgress<ReviewProgress>
    {
        public void Report(ReviewProgress value) { lock (list) list.Add(value); }
    }
}

/// <summary>Represents a temporary directory that is deleted on disposal.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibecheck-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    /// <summary>Gets the directory's full path.</summary>
    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
