using Vibecheck.Assist;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>
/// Tests of patterns new in a change, Claude's names for patterns, and the glossary's and placement's use of patterns.
/// See <c>docs/patterns.md</c> sections 4.5, 7.6 and 8.
/// </summary>
public class PatternNamingTests
{
    private const string Bus = """
        namespace A
        {
            public interface IConsumer<T> { }
            public interface IHandler<T> { }
            public interface IUnitOfWork { }
        }
        """;

    [Fact]
    public void ThreeAddedTypesSharingAKeyNothingAtBaseCarriesAreAPatternNewInTheChangeWithNoStep()
    {
        var before = Consumers(3);
        var after = new Dictionary<string, string>(before)
        {
            ["A/H1.cs"] = Handler("H1"), ["A/H2.cs"] = Handler("H2"), ["B/H3.cs"] = Handler("H3", "B"),
            // A fourth consumer carries a key that the consumers at base carry, so it joins their pattern and no new one.
            ["A/C4.cs"] = Consumer(4),
        };
        var cs = Snippets.Changeset(before, after);
        var created = Assert.Single(cs.NewPatterns);
        Assert.Equal(("implements:A.IHandler<>", "implements IHandler<>"), (created.Id, created.Name));
        Assert.Equal([new PatternOther("A.H1", "a"), new PatternOther("A.H2", "a"), new PatternOther("B.H3", "b")], created.Instances);
        Assert.Equal(["implements:A.IConsumer<>"], cs.Patterns.Select(p => p.Id));
        Assert.DoesNotContain(cs.Path, s => s.Kind == "pattern");

        // Two added types are too few, and a key that a type at base carries is not new.
        after.Remove("B/H3.cs");
        Assert.Empty(Snippets.Changeset(before, after).NewPatterns);
        before["A/H0.cs"] = Handler("H0");
        Assert.Empty(Snippets.Changeset(before, new Dictionary<string, string>(after) { ["B/H3.cs"] = Handler("H3", "B") }).NewPatterns);
    }

    [Fact]
    public void ThePatternIsNamedFromItsThreeMostTypicalInstancesAtBaseWhateverTheHead()
    {
        var before = Consumers(5);
        before["A/C5.cs"] = Consumer(5, working: false);
        var one = Snippets.Changeset(before, new Dictionary<string, string>(before) { ["A/C6.cs"] = Consumer(6) });
        var two = Snippets.Changeset(before, new Dictionary<string, string>(before) { ["A/C1.cs"] = Consumer(1, working: false), ["A/C7.cs"] = Consumer(7, working: false) });
        // C5 alone takes no unit of work, so it is the least typical, and the others tie and go by name.
        Assert.Equal(["A.C1", "A.C2", "A.C3"], Assert.Single(one.Patterns).Samples);
        Assert.Equal(Assert.Single(one.Patterns).Samples, Assert.Single(two.Patterns).Samples);
    }

    [Fact]
    public void ClaudeNamesEachPatternAfterTheSummaryUnlessTheMapNamesItAndTheRequestIsTheSameForEveryHeadOfOneBase()
    {
        var before = Consumers(3);
        var one = Context(before, new Dictionary<string, string>(before) { ["A/C4.cs"] = Consumer(4) });
        var two = Context(before, new Dictionary<string, string>(before) { ["A/C1.cs"] = Consumer(1, working: false) });
        var plan = NarrativePlanner.Plan(one.Changeset);
        Assert.Equal(["summary", "pattern:implements:A.IConsumer<>"], plan.Take(2).Select(b => b.Id));
        var batch = plan[1];
        Assert.Equal(["pattern:implements:A.IConsumer<>"], batch.Keys);
        Assert.Equal(NarrativeWriter.RequestKey(batch, one), NarrativeWriter.RequestKey(NarrativePlanner.Plan(two.Changeset)[1], two));

        // A map entry that gives both a name and a purpose leaves Claude nothing to write; one that gives a name alone does not.
        var named = Snippets.Map();
        named.Patterns.Add(new PatternSpec { Id = "consumer", Name = "Consumer", Purpose = "Handles a message.", Keys = ["implements:A.IConsumer<>"] });
        Assert.DoesNotContain(NarrativePlanner.Plan(Changeset(named, one)), b => b.Kind == "pattern");
        var nameOnly = Snippets.Map();
        nameOnly.Patterns.Add(new PatternSpec { Id = "consumer", Name = "Consumer", Keys = ["implements:A.IConsumer<>"] });
        Assert.Contains(NarrativePlanner.Plan(Changeset(nameOnly, one)), b => b.Kind == "pattern");
    }

    [Fact]
    public async Task ANameAndPurposeRestOnTheSamplesSourceAtBaseAndAnAnswerCitingAnotherFileIsDiscarded()
    {
        var before = Consumers(3);
        var context = Context(before, new Dictionary<string, string>(before) { ["A/C4.cs"] = Consumer(4) });
        var batch = NarrativePlanner.Plan(context.Changeset).Single(b => b.Kind == "pattern");
        var claude = new FakeClaude(
            """{ "name": "Consumer", "text": "Handles a message.", "files": ["A/C4.cs"] }""",
            """{ "name": "Consumer", "text": "Handles one message from the bus.", "files": ["A/C1.cs", "A/C2.cs"] }""");

        var narrative = Assert.Single(await NarrativeWriter.WriteAsync(batch, context, claude, null, _ => { }));

        Assert.Equal(("pattern:implements:A.IConsumer<>", "Consumer", "Handles one message from the bus."), (narrative.Key, narrative.Name, narrative.Text));
        Assert.Equal(["A/C1.cs", "A/C2.cs"], narrative.Cites);
        var prompt = claude.Requests[0].Prompt;
        Assert.Contains("- `implements:A.IConsumer<>`", prompt, StringComparison.Ordinal);
        Assert.Contains("3 types carried these keys", prompt, StringComparison.Ordinal);
        Assert.Contains("### `A.C1`\n\nDeclared in A/C1.cs.\n```csharp\n    public sealed class C1 : A.IConsumer<int>\n    {\n        public C1(A.IUnitOfWork work) { }\n"
            + "        public void Consume() { }\n    }\n```", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("A.C4", prompt, StringComparison.Ordinal);
        Assert.Equal(Prompts.Read("pattern.schema.json"), claude.Requests[0].Schema);
        Assert.Contains("the answer cites A/C4.cs, which does not declare one of the three instances", claude.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsortedInstanceOfAPatternGetsTheLeafOfItsNearestExampleAsAHint()
    {
        // The consumers in A take no unit of work, those in B do, and so does the unsorted one in D.
        var files = new Dictionary<string, string>
        {
            ["A/Bus.cs"] = Bus,
            ["A/C1.cs"] = Consumer(1, working: false), ["A/C2.cs"] = Consumer(2, working: false), ["A/C3.cs"] = Consumer(3, working: false),
            ["B/C4.cs"] = Consumer(4, ns: "B"), ["B/C5.cs"] = Consumer(5, ns: "B"),
            ["D/C9.cs"] = Consumer(9, ns: "D"),
        };
        var facts = Snippets.Facts(files);
        var unsorted = facts.Types.Where(t => t.Leaf == ArchitectureMap.Unsorted).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["D.C9"], unsorted);
        var hint = Assert.Single(TypePlacer.Hints(facts, unsorted));
        Assert.Equal(("D.C9", new PatternHint("implements IConsumer<>", "B.C4", "b")), (hint.Key, hint.Value));
    }

    private static NarrativeContext Context(Dictionary<string, string> before, Dictionary<string, string> after) =>
        new(Snippets.Changeset(before, after), Snippets.Analyze(before), Snippets.Analyze(after), "(no commit messages)\n")
        {
            ReadBase = paths => paths.Where(before.ContainsKey).ToDictionary(p => p, p => before[p]),
        };

    /// <summary>Returns the changeset of a context's change under another map.</summary>
    private static Changeset Changeset(ArchitectureMap map, NarrativeContext context) =>
        ChangesetBuilder.Build(Checker.Run(map, context.Base), Checker.Run(map, context.Head));

    private static Dictionary<string, string> Consumers(int count)
    {
        var files = new Dictionary<string, string> { ["A/Bus.cs"] = Bus };
        for (var i = 1; i <= count; i++) files[$"A/C{i}.cs"] = Consumer(i);
        return files;
    }

    private static string Consumer(int n, bool working = true, string ns = "A") =>
        $"namespace {ns}\n{{\n    public sealed class C{n} : A.IConsumer<int>\n    {{\n        {(working ? $"public C{n}(A.IUnitOfWork work) {{ }}\n        " : "")}public void Consume() {{ }}\n    }}\n}}\n";

    private static string Handler(string name, string ns = "A") => $"namespace {ns} {{ public sealed class {name} : A.IHandler<int> {{ }} }}\n";
}
