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
        var file = GitHunks.Collect(Path.Combine(repo.Path, "sub"), before, after, ["A.cs"]).Single();
        Assert.Equal("A.cs", file.Path);
        Assert.True(file.Trivial);
    }
}
