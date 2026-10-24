using Archmap.Diffing;
using Archmap.Format;
using Archmap.Reviewing;
using Xunit;

namespace Archmap.Tests;

public class WorkingTreeTests
{
    /// <summary>Commits a tracked file, a file to delete and a .gitignore, and returns the commit.</summary>
    private static string Committed(TempRepo repo)
    {
        repo.Write(".gitignore", "*.log\n");
        repo.Write("kept.txt", "one\n");
        repo.Write("gone.txt", "bye\n");
        return repo.Commit("one");
    }

    [Fact]
    public void SnapshotsTrackedUntrackedAndDeletedFilesWithoutTouchingTheIndexOrIgnoredFiles()
    {
        using var repo = new TempRepo();
        var head = Committed(repo);
        repo.Write("kept.txt", "two\n");
        repo.Write("staged.txt", "staged\n");
        repo.Git("add", "staged.txt");
        repo.Write("new.txt", "new\n");
        repo.Write("noise.log", "ignored\n");
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        var status = repo.Git("status", "--porcelain", "--untracked-files=all");

        var snapshot = WorkingTree.Snapshot(repo.Path);

        Assert.Equal(head, repo.Git("rev-parse", snapshot + "^"));
        Assert.Equal([".gitignore", "kept.txt", "new.txt", "staged.txt"], repo.Git("ls-tree", "--name-only", snapshot).Split('\n'));
        Assert.Equal("two", repo.Git("show", snapshot + ":kept.txt"));
        Assert.Equal(status, repo.Git("status", "--porcelain", "--untracked-files=all"));
        Assert.Equal("staged.txt", repo.Git("diff", "--cached", "--name-only"));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.True(WorkingTree.IsSnapshot(repo.Path, snapshot));
        Assert.False(WorkingTree.IsSnapshot(repo.Path, head));
    }

    [Fact]
    public void GivesTheSameCommitForTheSameTreeAndHeadForACleanTree()
    {
        using var repo = new TempRepo();
        var head = Committed(repo);
        Assert.Equal(head, WorkingTree.Snapshot(repo.Path));
        Assert.Equal(0, WorkingTree.ChangedPaths(repo.Path));

        repo.Write("kept.txt", "two\n");
        var first = WorkingTree.Snapshot(repo.Path);
        Assert.Equal(first, WorkingTree.Snapshot(repo.Path));
        repo.Write("kept.txt", "three\n");
        Assert.NotEqual(first, WorkingTree.Snapshot(repo.Path));
    }

    [Fact]
    public void ResolvesTheWorktreeNameAloneAndInRanges()
    {
        using var repo = new TempRepo();
        var head = Committed(repo);
        repo.Write("kept.txt", "two\n");
        var snapshot = WorkingTree.Snapshot(repo.Path);

        Assert.Equal((head, snapshot), RevisionChangeset.ResolveRange(repo.Path, WorkingTree.Name));
        Assert.Equal((head, snapshot), RevisionChangeset.ResolveRange(repo.Path, "HEAD.." + WorkingTree.Name));
        Assert.Equal((head, snapshot), RevisionChangeset.ResolveRange(repo.Path, "main..." + WorkingTree.Name));
        Assert.Equal(new RangeChoice("worktree", "Uncommitted changes", ":worktree", "1 path changed against HEAD"), RangeCatalog.List(repo.Path)[0]);
    }

    [Fact]
    public async Task ReviewsTheUncommittedWorkAgainstHead()
    {
        using var repo = new TempRepo();
        ReviewPipelineTests.WriteApp(repo, "class Thing { }\n");
        repo.Write("app/archmap.json", MapWriter.Write(TestData.Map()));
        var head = repo.Commit("one");
        ReviewPipelineTests.WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var (baseCommit, snapshot) = RevisionChangeset.ResolveRange(repo.Path, WorkingTree.Name);

        var result = await ReviewPipeline.RunAsync(new ReviewRequest(Path.Combine(repo.Path, "app"), baseCommit, snapshot) { Build = false }, ReviewPipelineTests.Fake([]));

        Assert.Equal($"{head[..7]}..working tree", result.Info.Range);
        Assert.Equal("Uncommitted changes", result.Info.Title);
        Assert.Equal("added", result.Changeset.Edges.Single(e => e.From == "a.x" && e.To == "b").Status);
        Assert.Equal("M app/" + TestData.X1, repo.Git("status", "--porcelain", "app/" + TestData.X1));
    }
}
