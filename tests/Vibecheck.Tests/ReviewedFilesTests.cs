using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Reviewing;
using Xunit;

namespace Vibecheck.Tests;

public class ReviewedFilesTests
{
    private const string Doc = "docs/guide.md";

    /// <summary>
    /// Returns a changeset in which X1 and Y1 are modified, Y1 only trivially, and a document is renamed without
    /// changes. X1's blob at head is the one given, so that two calls can stand for two pushes.
    /// </summary>
    private static Changeset Changeset(string x1Head = "x2")
    {
        var baseFacts = Checker.Run(TestData.Map(), TestData.Analysis());
        var files = TestData.Analysis().Files.Select(f => f.Path is TestData.X1 or TestData.Y1 ? f with { Hash = "changed" } : f).ToList();
        var head = Checker.Run(TestData.Map(), TestData.Analysis() with { Files = files });
        var changes = new List<GitChange>
        {
            new(TestData.X1, "modified", null, "x1", x1Head),
            new(TestData.Y1, "modified", null, "y1", "y2"),
            new(Doc, "renamed", "docs/old.md", "d1", "d1"),
        };
        var hunks = new List<FileHunks> { new(TestData.X1, [new Hunk("@@ -1 +1 @@", [])]), new(TestData.Y1, [new Hunk("@@ -1 +1 @@", [], true)], true) };
        return ChangesetBuilder.Build(baseFacts, head, hunks, changes);
    }

    [Fact]
    public void TrivialFilesAndUnchangedRenamesStartReviewedAndTheReviewersUnmarkWins()
    {
        var store = new ReviewedFiles(null);
        var cs = Changeset();
        Assert.Equal(new Dictionary<string, string> { [Doc] = "renamed", [TestData.Y1] = "trivial" }, store.Marks(cs, "app"));

        store.Set(cs, "app", [TestData.Y1], reviewed: false);
        store.Set(cs, "app", [TestData.X1, "not/changed.cs"], reviewed: true);
        Assert.Equal(new Dictionary<string, string> { [Doc] = "renamed", [TestData.X1] = "reviewer" }, store.Marks(cs, "app"));
    }

    [Fact]
    public void AMarkSurvivesAPushThatLeavesItsFileAloneAndClearsOnOneThatChangesIt()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "reviewed.json");
        new ReviewedFiles(path).Set(Changeset(), "app", [TestData.X1], reviewed: true);

        // The marks are read back from the file, as a later session would.
        var store = new ReviewedFiles(path);
        Assert.True(store.Marks(Changeset(), "app").ContainsKey(TestData.X1));
        Assert.False(store.Marks(Changeset(x1Head: "x3"), "app").ContainsKey(TestData.X1));
        // The same path under another directory of the repository is another file.
        Assert.False(store.Marks(Changeset(), ".").ContainsKey(TestData.X1));
    }
}
