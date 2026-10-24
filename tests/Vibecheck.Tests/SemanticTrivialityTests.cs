using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Reviewing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the kinds of trivial edit beyond token equality: renames, moves, usings and generated files. See <c>docs/attention.md</c> section 5.</summary>
public class SemanticTrivialityTests
{
    private const string Clock = """
        namespace A;

        public class Clock
        {
            public int Now(int zone) => zone;
        }
        """;

    private const string Caller = """
        namespace B;

        public class Caller
        {
            public int First(A.Clock clock) => clock.Now(1);

            public int Second(A.Clock clock)
            {
                var x = 2;
                return clock.Now(x);
            }
        }
        """;

    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);

    private static FileHunks HunksOf(Changeset cs, string path) => cs.Hunks.Single(h => h.Path == path);

    [Fact]
    public void ARenameConsequenceIsProvenLineByLineAndNamesItsDecision()
    {
        var before = Files(("src/Clock.cs", Clock), ("src/Caller.cs", Caller));
        var after = Files(("src/Clock.cs", Clock.Replace("Now", "Current")), ("src/Caller.cs", Caller.Replace("Now", "Current")));
        var cs = Snippets.Changeset(before, after);

        var caller = HunksOf(cs, "src/Caller.cs");
        Assert.True(caller.Trivial);
        Assert.Equal(new EditReason(EditReason.Rename, "A.Clock.Current", "A.Clock.Now", Decision: "src/Clock.cs#0"), caller.Reason);
        Assert.All(caller.Hunks, h => Assert.Equal(EditReason.Rename, h.Reason?.Kind));
        // The consequence is still listed under the rename's decision, which the reviewer reads once.
        var decision = Assert.Single(cs.Decisions);
        Assert.Equal("src/Clock.cs#0", decision.Id);
        Assert.True(decision.Public);
        Assert.Contains(decision.Consequences, c => c.File == "src/Caller.cs");
        Assert.Equal(["src/Clock.cs#0"], caller.Hunks[0].Follows);

        // The rename's own declaration is a decision, so its file does not start out reviewed.
        var clock = HunksOf(cs, "src/Clock.cs");
        Assert.Null(clock.Trivial);
        Assert.Null(clock.Reason);
        Assert.Equal(EditReason.Rename, ReviewedFiles.ToolReason(cs.Files.Single(f => f.Path == "src/Caller.cs"), caller));
        Assert.Null(ReviewedFiles.ToolReason(cs.Files.Single(f => f.Path == "src/Clock.cs"), clock));
    }

    [Fact]
    public void ALineThatChangesMoreThanTheNameIsNotARenameConsequence()
    {
        var before = Files(("src/Clock.cs", Clock), ("src/Caller.cs", Caller));
        var after = Files(("src/Clock.cs", Clock.Replace("Now", "Current")), ("src/Caller.cs", Caller.Replace("Now", "Current").Replace("Current(1)", "Current(2)")));
        var cs = Snippets.Changeset(before, after);
        var caller = HunksOf(cs, "src/Caller.cs");
        Assert.Null(caller.Trivial);
        Assert.Null(caller.Reason);
        // It still follows from the rename by the per-line rule of section 4.2, which the reviewer can see.
        Assert.NotNull(caller.Hunks[0].Follows);
    }

    [Fact]
    public void ADeclarationMovedBetweenFilesOfOneTypeIsTrivialAndNamesTheOtherFile()
    {
        const string part1 = "namespace A;\n\npublic partial class P\n{\n    public int One() => 1;\n\n    public int Two() => 2;\n}\n";
        const string part2 = "namespace A;\n\npublic partial class P\n{\n    public int Three() => 3;\n}\n";
        var before = Files(("src/P1.cs", part1), ("src/P2.cs", part2));
        var after = Files(("src/P1.cs", part1.Replace("\n\n    public int Two() => 2;", "")), ("src/P2.cs", part2.Replace("Three() => 3;", "Three() => 3;\n\n    public int Two() => 2;")));
        var cs = Snippets.Changeset(before, after);

        Assert.Equal(new EditReason(EditReason.Moved, Path: "src/P2.cs"), HunksOf(cs, "src/P1.cs").Reason);
        Assert.Equal(new EditReason(EditReason.Moved, Path: "src/P1.cs"), HunksOf(cs, "src/P2.cs").Reason);
        Assert.Empty(cs.Decisions);
    }

    [Fact]
    public void MembersReorderedWithinAFileAreTrivial()
    {
        const string text = "namespace A;\n\npublic class R\n{\n    public int One() => 1;\n\n    public int Two() => 2;\n\n    public int Three() => 3;\n}\n";
        const string reordered = "namespace A;\n\npublic class R\n{\n    public int Two() => 2;\n\n    public int Three() => 3;\n\n    public int One() => 1;\n}\n";
        var cs = Snippets.Changeset(Files(("src/R.cs", text)), Files(("src/R.cs", reordered)));
        var file = HunksOf(cs, "src/R.cs");
        Assert.True(file.Trivial);
        Assert.Equal(EditReason.Reordered, file.Reason?.Kind);
    }

    [Fact]
    public void AMovedDeclarationWhoseBodyChangedIsNotTrivial()
    {
        const string text = "namespace A;\n\npublic class R\n{\n    public int One() => 1;\n\n    public int Two() => 2;\n}\n";
        const string changed = "namespace A;\n\npublic class R\n{\n    public int Two() => 2;\n\n    public int One() => 10;\n}\n";
        var cs = Snippets.Changeset(Files(("src/R.cs", text)), Files(("src/R.cs", changed)));
        Assert.Null(HunksOf(cs, "src/R.cs").Trivial);
    }

    [Fact]
    public void UsingDirectivesAloneAreTrivial()
    {
        var before = Files(("src/Clock.cs", Clock));
        var after = Files(("src/Clock.cs", "using System;\n\n" + Clock));
        var file = HunksOf(Snippets.Changeset(before, after), "src/Clock.cs");
        Assert.True(file.Trivial);
        Assert.Equal(new EditReason(EditReason.Usings), file.Reason);
        Assert.Equal(EditReason.Usings, file.Hunks.Single().Reason?.Kind);

        // A using that arrives with a code change is read with it.
        var mixed = HunksOf(Snippets.Changeset(before, Files(("src/Clock.cs", "using System;\n\n" + Clock.Replace("=> zone", "=> zone + 1")))), "src/Clock.cs");
        Assert.Null(mixed.Trivial);
    }

    [Fact]
    public void GeneratedFilesAreSetAsideByNameOrHeader()
    {
        const string designer = "namespace A;\n\npublic partial class Form\n{\n    private int size = 1;\n}\n";
        var before = Files(("src/Form.Designer.cs", designer), ("src/Form.cs", "namespace A;\n\npublic partial class Form\n{\n    public int Size => size;\n}\n"));
        var after = Files(("src/Form.Designer.cs", designer.Replace("= 1", "= 2")), ("src/Form.cs", before["src/Form.cs"]),
            ("src/Made.cs", "// <auto-generated />\nnamespace A;\n\npublic class Made\n{\n}\n"));
        var cs = Snippets.Changeset(before, after);

        var byName = HunksOf(cs, "src/Form.Designer.cs");
        Assert.Equal(new EditReason(EditReason.Generated), byName.Reason);
        // The edit changes code, so the hunk is not trivial; the file is set aside all the same.
        Assert.Null(byName.Trivial);
        Assert.Null(byName.Hunks.Single().Trivial);
        Assert.Equal(new EditReason(EditReason.Generated), HunksOf(cs, "src/Made.cs").Reason);
        Assert.True(TrivialEdits.IsGenerated("x/Thing.g.cs", null));
        Assert.False(TrivialEdits.IsGenerated("x/Thing.cs", "namespace A;\n"));

        // The tool's reasons mark the files, and the reviewer's unmark wins over each.
        var store = new ReviewedFiles(null);
        var marks = store.Marks(cs, ".");
        Assert.Equal(EditReason.Generated, marks["src/Form.Designer.cs"]);
        Assert.Equal(EditReason.Generated, marks["src/Made.cs"]);
        store.Set(cs, ".", ["src/Form.Designer.cs"], reviewed: false);
        Assert.False(store.Marks(cs, ".").ContainsKey("src/Form.Designer.cs"));
    }

    [Fact]
    public void ReasonsRoundTripThroughJson()
    {
        var before = Files(("src/Clock.cs", Clock), ("src/Caller.cs", Caller));
        var after = Files(("src/Clock.cs", Clock.Replace("Now", "Current")), ("src/Caller.cs", Caller.Replace("Now", "Current")));
        var cs = Snippets.Changeset(before, after);
        var back = FactsJson.Deserialize<Changeset>(FactsJson.Serialize(cs));
        Assert.Equal(HunksOf(cs, "src/Caller.cs").Reason, HunksOf(back, "src/Caller.cs").Reason);
        Assert.Equal(HunksOf(cs, "src/Caller.cs").Hunks[0].Reason, HunksOf(back, "src/Caller.cs").Hunks[0].Reason);
    }
}
