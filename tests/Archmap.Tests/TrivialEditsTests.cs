using System.Diagnostics;
using Archmap.Checking;
using Archmap.Diffing;
using Xunit;

namespace Archmap.Tests;

public class TrivialEditsTests
{
    /// <summary>Returns the hunks of a file as one hunk that covers both versions whole.</summary>
    private static FileHunks Whole(string before, string after) =>
        new("a.cs", [new Hunk($"@@ -1,{Lines(before)} +1,{Lines(after)} @@", [])]);

    private static int Lines(string text) => text.Split('\n').Length;

    private static bool? MarkWhole(string before, string after) => TrivialEdits.Mark(Whole(before, after), before, after).Trivial;

    [Fact]
    public void ACommentOnlyEditIsTrivial()
    {
        const string before = "class A\n{\n    // Old words.\n    int X() => 1;\n}";
        const string after = "class A\n{\n    /// <summary>New words.</summary>\n    /* and more */\n    int X() => 1;\n}";
        var marked = TrivialEdits.Mark(Whole(before, after), before, after);
        Assert.True(marked.Trivial);
        Assert.True(marked.Hunks.Single().Trivial);
    }

    [Fact]
    public void ReflowingCodeIsTrivial()
    {
        Assert.True(MarkWhole("class A { int X() => Math.Max(1, 2); }", "class A\n{\n    int X() =>\n        Math.Max(\n            1,\n            2);\n}"));
    }

    [Fact]
    public void RegionAndPragmaLinesAreTrivial()
    {
        Assert.True(MarkWhole("class A { int X; }", "#pragma warning disable CS0169\nclass A\n{\n#region Fields\n    int X;\n#endregion\n}"));
    }

    [Fact]
    public void ACodeEditIsNotTrivial()
    {
        Assert.Null(MarkWhole("class A { int X() => 1; }", "class A { int X() => 2; }"));
    }

    [Fact]
    public void CommentMarkersInsideAStringAreCode()
    {
        Assert.Null(MarkWhole("class A { string S = \"a // b\"; }", "class A { string S = \"a // c\"; }"));
    }

    [Fact]
    public void ConditionalCompilationIsCode()
    {
        // Code that the parse leaves out may be compiled under other symbols, so editing it is not trivial.
        Assert.Null(MarkWhole("class A {\n#if DEBUG\n int X;\n#endif\n}", "class A {\n#if DEBUG\n int Y;\n#endif\n}"));
        Assert.Null(MarkWhole("class A {\n#if DEBUG\n int X;\n#endif\n}", "class A {\n#if TRACE\n int X;\n#endif\n}"));
        Assert.Null(MarkWhole("class A { }", "#nullable enable\nclass A { }"));
    }

    [Fact]
    public void EachHunkIsMarkedOnItsOwn()
    {
        const string before = "class A\n{\n    // One.\n    int X;\n\n\n\n\n\n\n    int Y = 1;\n}";
        const string after = "class A\n{\n    // Two.\n    int X;\n\n\n\n\n\n\n    int Y = 2;\n}";
        var file = new FileHunks("a.cs", [new Hunk("@@ -2,3 +2,3 @@", []), new Hunk("@@ -10,2 +10,2 @@", [])]);
        var marked = TrivialEdits.Mark(file, before, after);
        Assert.Equal([true, null], marked.Hunks.Select(h => h.Trivial));
        Assert.Null(marked.Trivial);
    }

    [Fact]
    public void NodeContentCountsTrivialFiles()
    {
        var baseFacts = Checker.Run(TestData.Map(), TestData.Analysis(), new CheckOptions { Commit = "base" });
        var files = TestData.Analysis().Files.Select(f => f.Path is TestData.Y1 or TestData.X1 ? f with { Hash = "changed" } : f).ToList();
        var head = Checker.Run(TestData.Map(), TestData.Analysis() with { Files = files });
        var hunks = new List<FileHunks> { new(TestData.Y1, [new Hunk("@@ -1 +1 @@", [], true)], true), new(TestData.X1, [new Hunk("@@ -1 +1 @@", [])]) };
        var cs = ChangesetBuilder.Build(baseFacts, head, hunks);
        Assert.Equal((1, 1), (cs.Content["a.y"].Modified, cs.Content["a.y"].Trivial));
        Assert.Equal((1, 0), (cs.Content["a.x"].Modified, cs.Content["a.x"].Trivial));
        Assert.Equal((2, 1), (cs.Content["a"].Modified, cs.Content["a"].Trivial));
        var back = FactsJson.Deserialize<Changeset>(FactsJson.Serialize(cs));
        Assert.True(back.Hunks[0].Trivial);
        Assert.Null(back.Hunks[1].Hunks[0].Trivial);
    }

    [Fact]
    public void CollectMarksFilesFromGit()
    {
        var repo = Directory.CreateTempSubdirectory("archmap-trivial-").FullName;
        try
        {
            void Git(params string[] args)
            {
                var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { "-C", repo, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args)) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                var error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, error);
            }
            Git("init", "-q");
            File.WriteAllText(Path.Combine(repo, "Doc.cs"), "class Doc\n{\n    // Old.\n    int X;\n}\n");
            File.WriteAllText(Path.Combine(repo, "Code.cs"), "class Code { int X = 1; }\n");
            Git("add", ".");
            Git("commit", "-q", "-m", "base");
            File.WriteAllText(Path.Combine(repo, "Doc.cs"), "class Doc\n{\n    // New, and longer.\n    int X;\n}\n");
            File.WriteAllText(Path.Combine(repo, "Code.cs"), "class Code { int X = 2; }\n");
            File.WriteAllText(Path.Combine(repo, "New.cs"), "// Only a comment.\n");
            Git("add", ".");
            Git("commit", "-q", "-m", "head");
            var files = GitHunks.Collect(repo, "HEAD~1", "HEAD", ["Doc.cs", "Code.cs", "New.cs"]).ToDictionary(f => f.Path);
            Assert.True(files["Doc.cs"].Trivial);
            Assert.Null(files["Code.cs"].Trivial);
            // A file at one end only is added or removed, which is never trivial.
            Assert.Null(files["New.cs"].Trivial);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(repo, true);
        }
    }
}
