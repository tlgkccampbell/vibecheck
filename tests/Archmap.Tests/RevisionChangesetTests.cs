using System.Collections.Concurrent;
using Archmap.Analysis;
using Archmap.Diffing;
using Archmap.Format;
using Archmap.Reviewing;
using Xunit;

namespace Archmap.Tests;

public class RevisionChangesetTests
{
    /// <summary>
    /// Returns an analyzer that stands in for Roslyn. It reports the synthetic analysis with each file's hash taken from
    /// its text in the checkout, and adds a call from Thing to Service when X1.cs mentions Service.
    /// </summary>
    private static SolutionAnalyzer Fake(ConcurrentBag<string> roots) => (root, solution, progress, ct) =>
    {
        roots.Add(root);
        var x1 = File.ReadAllText(Path.Combine(root, TestData.X1));
        var extra = x1.Contains("Service") ? new[] { new RawDependency(TestData.Thing, TestData.Service, "calls", "B.Service.Run", TestData.X1, 2) } : [];
        var analysis = TestData.Analysis(extra);
        return Task.FromResult(analysis with
        {
            Files = analysis.Files.Select(f => f with { Hash = File.ReadAllText(Path.Combine(root, f.Path)).GetHashCode().ToString() }).ToList(),
        });
    };

    /// <summary>Writes the synthetic solution's files under <c>app/</c>, with X1.cs holding the text given.</summary>
    private static void WriteApp(TempRepo repo, string x1)
    {
        repo.Write("app/Example.sln", "");
        foreach (var path in new[] { TestData.X2, TestData.Y1, TestData.B1, TestData.S1 }) repo.Write("app/" + path, "class C { }\n");
        repo.Write("app/" + TestData.X1, x1);
    }

    private static ChangesetRequest Request(TempRepo repo, string baseCommit, string headCommit)
    {
        var map = TestData.Map();
        return new ChangesetRequest(Path.Combine(repo.Path, "app"), map, MapWriter.Write(map), null, baseCommit, headCommit, Build: false);
    }

    [Fact]
    public async Task ChecksOutEachRevisionAndDiffsThemWithOneMap()
    {
        using var repo = new TempRepo();
        WriteApp(repo, "class Thing { }\n");
        var before = repo.Commit("one");
        WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var after = repo.Commit("two");

        var roots = new ConcurrentBag<string>();
        var result = await RevisionChangeset.BuildAsync(Request(repo, before, after), Fake(roots));

        var cs = result.Changeset;
        Assert.Equal((before, after), (cs.Base, cs.Head));
        var edge = cs.Edges.Single(e => e.From == "a.x" && e.To == "b");
        Assert.Equal((0, 1, "added"), (edge.Base, edge.Head, edge.Status));
        Assert.Equal("modified", cs.Files.Single(f => f.Path == TestData.X1).Status);
        Assert.Equal([TestData.X1], cs.Hunks.Select(h => h.Path));
        Assert.Equal(2, roots.Count);
        Assert.All(roots, r => Assert.False(r.StartsWith(repo.Path, StringComparison.OrdinalIgnoreCase)));
        Assert.All(roots, r => Assert.False(Directory.Exists(r)));
        Assert.Single(repo.Git("worktree", "list").Split('\n'));
    }

    [Fact]
    public async Task RemovesTheWorktreesWhenAnalysisFails()
    {
        using var repo = new TempRepo();
        WriteApp(repo, "class Thing { }\n");
        var commit = repo.Commit("one");
        var roots = new ConcurrentBag<string>();
        SolutionAnalyzer failing = (root, solution, progress, ct) => { roots.Add(root); throw new InvalidOperationException("broken"); };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RevisionChangeset.BuildAsync(Request(repo, commit, commit), failing));
        Assert.NotEmpty(roots);
        Assert.All(roots, r => Assert.False(Directory.Exists(r)));
        Assert.Single(repo.Git("worktree", "list").Split('\n'));
    }

    [Fact]
    public void ResolvesRangesTheWayGitDoes()
    {
        using var repo = new TempRepo();
        repo.Write("f.txt", "1");
        var first = repo.Commit("one");
        repo.Git("checkout", "--quiet", "-b", "feature");
        repo.Write("f.txt", "2");
        var feature = repo.Commit("two");
        repo.Git("checkout", "--quiet", "main");
        repo.Write("g.txt", "3");
        var main = repo.Commit("three");

        Assert.Equal((first, feature), RevisionChangeset.ResolveRange(repo.Path, "main...feature"));
        Assert.Equal((main, feature), RevisionChangeset.ResolveRange(repo.Path, "main..feature"));
        Assert.Equal((feature, main), RevisionChangeset.ResolveRange(repo.Path, "feature"));
        Assert.Equal((feature, main), RevisionChangeset.ResolveRange(repo.Path, "feature.."));
        Assert.Equal((first, feature), RevisionChangeset.ResolveRange(repo.Path, first, "feature"));
        Assert.Throws<ArgumentException>(() => RevisionChangeset.ResolveRange(repo.Path, "nonesuch"));
    }
}
