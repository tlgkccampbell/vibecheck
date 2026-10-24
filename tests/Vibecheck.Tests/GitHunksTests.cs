using Vibecheck.Diffing;
using Xunit;

namespace Vibecheck.Tests;

public class GitHunksTests
{
    [Fact]
    public void ParsesUnifiedDiffIntoHunksPerFile()
    {
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            index 1111111..2222222 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,3 +1,4 @@
             using X;
            -old();
            +new();
            +more();

            diff --git a/src/New.cs b/src/New.cs
            new file mode 100644
            --- /dev/null
            +++ b/src/New.cs
            @@ -0,0 +1,2 @@
            +class New
            +{ }
            \ No newline at end of file
            diff --git a/src/Gone.cs b/src/Gone.cs
            deleted file mode 100644
            --- a/src/Gone.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            """;
        var files = GitHunks.Parse(diff);
        Assert.Equal(["src/A.cs", "src/New.cs", "src/Gone.cs"], files.Select(f => f.Path));
        var a = files[0].Hunks.Single();
        Assert.Equal("@@ -1,3 +1,4 @@", a.Header);
        Assert.Equal([[" ", "using X;"], ["-", "old();"], ["+", "new();"], ["+", "more();"], [" ", ""]], a.Lines);
        Assert.Equal(2, files[1].Hunks.Single().Lines.Count);
        Assert.Equal(["-", "gone"], files[2].Hunks.Single().Lines.Single());
    }

    [Fact]
    public void PathsAreRelativeToTheDirectoryGivenWhenItIsBelowTheRepositoryTop()
    {
        using var repo = new TempRepo();
        repo.Write("sub/A.cs", "class A { }\n");
        var before = repo.Commit("one");
        repo.Write("sub/A.cs", "// A comment.\nclass A { }\n");
        var after = repo.Commit("two");
        var dir = Path.Combine(repo.Path, "sub");
        var file = GitHunks.Collect(dir, before, after, GitHunks.Changes(dir, before, after)).Single();
        Assert.Equal("A.cs", file.Path);
        Assert.True(file.Trivial);
    }

    [Fact]
    public void AMovedFileIsOneRenameWithItsEditsAsHunks()
    {
        using var repo = new TempRepo();
        var lines = Enumerable.Range(1, 12).Select(i => $"    int F{i};").ToList();
        repo.Write("src/Old.cs", "class C\n{\n" + string.Join("\n", lines) + "\n}\n");
        repo.Write("src/Same.cs", "class Same { }\n");
        var before = repo.Commit("one");
        File.Delete(Path.Combine(repo.Path, "src/Old.cs"));
        lines[5] = "    int Changed;";
        repo.Write("lib/New.cs", "class C\n{\n" + string.Join("\n", lines) + "\n}\n");
        File.Move(Path.Combine(repo.Path, "src/Same.cs"), Path.Combine(repo.Path, "lib/Same.cs"));
        repo.Write("docs/notes.md", "Notes.\n");
        var after = repo.Commit("two");

        var changes = GitHunks.Changes(repo.Path, before, after).ToDictionary(c => c.Path);
        Assert.Equal(["docs/notes.md", "lib/New.cs", "lib/Same.cs"], changes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("renamed", "src/Old.cs"), (changes["lib/New.cs"].Status, changes["lib/New.cs"].PreviousPath));
        Assert.NotEqual(changes["lib/New.cs"].BlobBase, changes["lib/New.cs"].BlobHead);
        Assert.Equal(("renamed", "src/Same.cs"), (changes["lib/Same.cs"].Status, changes["lib/Same.cs"].PreviousPath));
        Assert.Equal(changes["lib/Same.cs"].BlobBase, changes["lib/Same.cs"].BlobHead);
        Assert.Equal(("added", null), (changes["docs/notes.md"].Status, changes["docs/notes.md"].BlobBase));

        var hunks = GitHunks.Collect(repo.Path, before, after, changes.Values.ToList()).ToDictionary(f => f.Path);
        // The moved file's diff holds the one line that changed, not every line deleted and added again.
        Assert.Equal([["-", "    int F6;"], ["+", "    int Changed;"]], hunks["lib/New.cs"].Hunks.SelectMany(h => h.Lines).Where(l => l[0] != " "));
        Assert.Null(hunks["lib/New.cs"].Trivial);
        Assert.False(hunks.ContainsKey("lib/Same.cs"));
        Assert.True(hunks.ContainsKey("docs/notes.md"));
    }
}
