using Vibecheck.Analysis;
using Vibecheck.Assist;
using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Stores narratives in memory, and counts the writes.</summary>
internal sealed class MemoryNarrativeCache : INarrativeCache
{
    public Dictionary<string, IReadOnlyList<Narrative>> Entries { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<Narrative>? ReadNarratives(string key) { lock (Entries) return Entries.GetValueOrDefault(key); }

    public void WriteNarratives(string key, IReadOnlyList<Narrative> narratives) { lock (Entries) Entries[key] = narratives; }
}

public class NarrativeTests
{
    /// <summary>The narrative of the new edge from <c>a</c> to <c>b</c>, citing its one type pair.</summary>
    private const string EdgeAnswer = """{ "from": "a", "to": "b", "text": "Thing now runs the service.", "pairs": [ { "from": "A.X.Thing", "to": "B.Service" } ] }""";

    /// <summary>The narrative of node <c>a</c>, citing its changed file.</summary>
    private const string NodeAnswer = """{ "id": "a", "text": "A gained a call.", "files": ["src/A/X/X1.cs"] }""";

    /// <summary>
    /// Returns the changeset of the synthetic solution when Thing starts calling Service: X1.cs is modified, so leaf
    /// <c>a.x</c> and container <c>a</c> change, and the edge from <c>a.x</c> to <c>b</c> is added.
    /// </summary>
    internal static NarrativeContext Context(ArchitectureMap? map = null)
    {
        map ??= TestData.Map();
        var before = TestData.Analysis();
        var after = TestData.Analysis([new RawDependency(TestData.Thing, TestData.Service, "calls", "B.Service.Run", TestData.X1, 2)]);
        after = after with { Files = after.Files.Select(f => f.Path == TestData.X1 ? f with { Hash = "changed" } : f).ToList() };
        var hunks = new List<FileHunks>
        {
            new(TestData.X1, [new Hunk("@@ -1 +1 @@", [["-", "class Thing { }"], ["+", "class Thing { void Go() => Service.Run(); }"]])]),
        };
        var changeset = ChangesetBuilder.Build(Checker.Run(map, before), Checker.Run(map, after), hunks);
        return new NarrativeContext(changeset, before, after, "- abc1234 Call the service\n  Thing needs the service to run.\n");
    }

    [Fact]
    public void PlansTheSummaryThenEachLevelWithItsChangedEdgesAndNodes()
    {
        var plan = NarrativePlanner.Plan(Context().Changeset);

        Assert.Equal(["summary", "level:root/1", "level:a/1"], plan.Select(b => b.Id));
        Assert.Equal(["edge:a→b", "node:a"], plan[1].Keys);
        Assert.Equal(["node:a.x"], plan[2].Keys);
        var edge = plan[1].Edges.Single();
        Assert.Equal((0, 1, "added"), (edge.Base, edge.Head, edge.Status));
        Assert.Equal(new PairChange(TestData.Thing, TestData.Service, 0, 1), Assert.Single(edge.Evidence));
    }

    [Fact]
    public void OpensWithTheTopLevelViewAndExplainsTheEdgesBetweenItsGroups()
    {
        var map = TestData.Map();
        map.Views["by-role"] = new ViewSpec
        {
            Name = "By role", Level = Identifiers.Root,
            Groups = new(StringComparer.Ordinal)
            {
                ["work"] = new GroupSpec { Name = "Work", Leaves = ["a.x", "a.y"] },
                ["serve"] = new GroupSpec { Name = "Serve", Leaves = ["b", "shared"] },
            },
        };

        var plan = NarrativePlanner.Plan(Context(map).Changeset, batchSize: 1);

        Assert.Equal(["summary", "view:by-role/1", "level:root/1", "level:root/2", "level:a/1"], plan.Select(b => b.Id));
        Assert.Equal(["edge:g:by-role/work→g:by-role/serve"], plan[1].Keys);
        Assert.Equal(["edge:a→b"], plan[2].Keys);
        Assert.Equal(["node:a"], plan[3].Keys);
    }

    [Fact]
    public async Task WritesABatchFromTheEvidenceAndTheCodeAtHead()
    {
        var context = Context() with { WorkingDirectory = "C:/somewhere/head", Model = "sonnet" };
        var batch = NarrativePlanner.Plan(context.Changeset)[1];
        var claude = new FakeClaude($$"""{ "edges": [ {{EdgeAnswer}} ], "nodes": [ {{NodeAnswer}} ] }""");
        var accepted = new List<Narrative>();

        var narratives = await NarrativeWriter.WriteAsync(batch, context, claude, null, accepted.Add);

        Assert.Equal(["edge:a→b", "node:a"], narratives.Select(n => n.Key));
        Assert.Equal(narratives, accepted);
        Assert.Equal(["A.X.Thing → B.Service"], narratives[0].Cites);
        var request = Assert.Single(claude.Requests);
        Assert.Equal(("C:/somewhere/head", "sonnet"), (request.WorkingDirectory, request.Model));
        Assert.Equal(["Read", "Grep", "Glob"], request.Tools);
        Assert.Contains("added: calls `B.Service.Run` at src/A/X/X1.cs:2", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("+class Thing { void Go() => Service.Run(); }", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Thing needs the service to run.", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("somewhere", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsksOnceMoreOnItsOwnForANarrativeThatCitesForeignEvidence()
    {
        var context = Context();
        var batch = NarrativePlanner.Plan(context.Changeset)[1];
        var claude = new FakeClaude(
            $$"""{ "edges": [ { "from": "a", "to": "b", "text": "Made up.", "pairs": [ { "from": "A.Y.Other", "to": "B.Service" } ] } ], "nodes": [ {{NodeAnswer}} ] }""",
            $$"""{ "edges": [ {{EdgeAnswer}} ], "nodes": [] }""");

        var narratives = await NarrativeWriter.WriteAsync(batch, context, claude, null, _ => { });

        Assert.Equal(["node:a", "edge:a→b"], narratives.Select(n => n.Key));
        Assert.Equal("Thing now runs the service.", narratives[1].Text);
        var retry = claude.Requests[1].Prompt;
        Assert.Contains("A.Y.Other → B.Service, which is not among the edge's changed type pairs", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("### `a`, A", retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropsANarrativeThatFailsTwiceAndOneThatCitesAFileOutsideItsNode()
    {
        var context = Context();
        var batch = NarrativePlanner.Plan(context.Changeset)[1];
        var claude = new FakeClaude(
            """{ "edges": [], "nodes": [ { "id": "a", "text": "Wrong file.", "files": ["src/B/B1.cs"] } ] }""",
            """{ "edges": [], "nodes": [ { "id": "a", "text": "Still wrong.", "files": [] } ] }""");

        var narratives = await NarrativeWriter.WriteAsync(batch, context, claude, null, _ => { });

        Assert.Empty(narratives);
        Assert.Contains("the answer gave no narrative for it", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("src/B/B1.cs, which is not under the node", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(2, claude.Requests.Count);
    }

    [Fact]
    public async Task ChecksThatTheSummaryCitesPartsOfTheMap()
    {
        var context = Context();
        var batch = NarrativePlanner.Plan(context.Changeset)[0];
        var claude = new FakeClaude("""{ "text": "It calls the service.", "parts": ["nowhere"] }""", """{ "text": "It calls the service.", "parts": ["a", "b"] }""");

        var narratives = await NarrativeWriter.WriteAsync(batch, context, claude, null, _ => { });

        Assert.Equal(new[] { "a", "b" }, Assert.Single(narratives).Cites);
        Assert.Contains("- A → B: added, 0 → 1 references, such as `A.X.Thing` → `B.Service`", claude.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("the summary cites nowhere, which the map does not have", claude.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsAStoredBatchUnlessToldToRegenerateAndUpdatesItWithOneRegeneratedNarrative()
    {
        var context = Context();
        var batch = NarrativePlanner.Plan(context.Changeset)[1];
        var cache = new MemoryNarrativeCache();
        var claude = new FakeClaude(
            $$"""{ "edges": [ {{EdgeAnswer}} ], "nodes": [ {{NodeAnswer}} ] }""",
            $$"""{ "edges": [], "nodes": [ { "id": "a", "text": "A, again.", "files": ["src/A/X/X1.cs"] } ] }""",
            $$"""{ "edges": [ {{EdgeAnswer}} ], "nodes": [ {{NodeAnswer}} ] }""");

        await NarrativeWriter.WriteAsync(batch, context, claude, cache, _ => { });
        var again = await NarrativeWriter.WriteAsync(batch, context, claude, cache, _ => { });
        Assert.Single(claude.Requests);
        Assert.Equal(2, again.Count);

        var one = await NarrativeWriter.RegenerateAsync(batch, "node:a", context, claude, cache);
        Assert.Equal("A, again.", one!.Text);
        Assert.DoesNotContain("### `a` → `b`", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("A, again.", (await NarrativeWriter.WriteAsync(batch, context, claude, cache, _ => { })).Select(n => n.Text));

        await NarrativeWriter.WriteAsync(batch, context, claude, cache, _ => { }, regenerate: true);
        Assert.Equal(3, claude.Requests.Count);
        Assert.Single(cache.Entries);
        Assert.NotEqual(NarrativeWriter.RequestKey(batch, context), NarrativeWriter.RequestKey(batch, context with { Model = "haiku" }));
    }

    [Fact]
    public async Task RunsEveryBatchAndCountsWhatWasWrittenAndDropped()
    {
        var context = Context();
        var plan = NarrativePlanner.Plan(context.Changeset);
        var claude = new FakeClaude(r => r.Schema.Contains("\"parts\"", StringComparison.Ordinal)
            ? """{ "text": "Summary.", "parts": ["a"] }"""
            : $$"""{ "edges": [ {{EdgeAnswer}} ], "nodes": [ {{NodeAnswer}} ] }""");
        var accepted = new List<Narrative>();

        var tally = await NarrativeWriter.RunAsync(plan, context, claude, null, n => { lock (accepted) accepted.Add(n); });

        Assert.Equal((3, 1), (tally.Written, tally.Dropped));
        Assert.Empty(tally.Failures);
        Assert.Equal(["edge:a→b", "node:a", "summary"], accepted.Select(n => n.Key).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Returns the context of a changeset that adds an undocumented <c>GammaConsumer</c> beside two consumers, one of
    /// them documented, and changes <c>DeltaStore</c>, whose term the documented <c>IStore</c> already defines.
    /// </summary>
    private static NarrativeContext TermContext()
    {
        static string Class(string name, string doc = "") => $"namespace A;\n\n{doc}public class {name}\n{{\n}}\n";
        var before = new Dictionary<string, string>
        {
            ["src/AlphaConsumer.cs"] = Class("AlphaConsumer", "/// <summary>Consumes alpha.</summary>\n"),
            ["src/BetaConsumer.cs"] = Class("BetaConsumer"),
            ["src/IStore.cs"] = "namespace A;\n\n/// <summary>Keeps things.</summary>\npublic interface IStore\n{\n}\n",
            ["src/DeltaStore.cs"] = Class("DeltaStore : IStore"),
            ["src/EpsilonStore.cs"] = Class("EpsilonStore : IStore"),
        };
        var after = new Dictionary<string, string>(before)
        {
            ["src/GammaConsumer.cs"] = Class("GammaConsumer"),
            ["src/DeltaStore.cs"] = "namespace A;\n\npublic class DeltaStore : IStore\n{\n    public int Count;\n}\n",
        };
        return new NarrativeContext(Snippets.Changeset(before, after), Snippets.Analyze(before), Snippets.Analyze(after), "(no commit messages)\n");
    }

    [Fact]
    public void PlansTheDefinitionsOfUndocumentedTermsAfterTheLevelsAndViews()
    {
        var context = TermContext();
        var plan = NarrativePlanner.Plan(context.Changeset);

        Assert.Equal(["Consumer", "Store"], context.Changeset.Glossary.Select(t => t.Name));
        var terms = Assert.Single(plan, b => b.Kind == "term");
        Assert.Same(terms, plan[^1]);
        Assert.Equal(("term:root/1", "root"), (terms.Id, terms.Location));
        // IStore documents "Store", so only "Consumer" is defined.
        Assert.Equal(["term:consumer"], terms.Keys);
    }

    [Fact]
    public async Task DefinesATermFromTheTypesThatHoldItAndDiscardsADefinitionThatCitesAnotherFile()
    {
        var context = TermContext();
        var batch = NarrativePlanner.Plan(context.Changeset)[^1];
        var claude = new FakeClaude(
            """{ "terms": [ { "key": "term:consumer", "text": "Made up.", "files": ["src/DeltaStore.cs"] } ] }""",
            """{ "terms": [ { "key": "term:consumer", "text": "Takes in one kind of message.", "files": ["src/AlphaConsumer.cs", "src/GammaConsumer.cs"] } ] }""");

        var narratives = await NarrativeWriter.WriteAsync(batch, context, claude, null, _ => { });

        var definition = Assert.Single(narratives);
        Assert.Equal(("term:consumer", "Takes in one kind of message."), (definition.Key, definition.Text));
        Assert.Equal(["src/AlphaConsumer.cs", "src/GammaConsumer.cs"], definition.Cites);
        var prompt = claude.Requests[0].Prompt;
        Assert.Contains("### `term:consumer`\n\n\"Consumer\", in the names of 3 types in 3 families, 1 of them added or changed by the change. Among them:\n", prompt, StringComparison.Ordinal);
        // The changed type comes first, and then the others, each with its summary when it has one.
        Assert.Contains("- `A.GammaConsumer`, added or changed, declared in src/GammaConsumer.cs in A, with no documentation\n"
            + "- `A.AlphaConsumer`, declared in src/AlphaConsumer.cs in A: Consumes alpha.\n"
            + "- `A.BetaConsumer`, declared in src/BetaConsumer.cs in A, with no documentation\n", prompt, StringComparison.Ordinal);
        Assert.Equal(Prompts.Read("terms.schema.json"), claude.Requests[0].Schema);
        Assert.Contains("the definition cites src/DeltaStore.cs, which does not declare one of the types listed for it", claude.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscardsADefinitionThatDescribesTheNamesRatherThanTheConcept()
    {
        var context = TermContext();
        var batch = NarrativePlanner.Plan(context.Changeset)[^1];
        var claude = new FakeClaude(
            """{ "terms": [ { "key": "term:consumer", "text": "A type whose name holds \"Consumer\" consumes messages.", "files": ["src/AlphaConsumer.cs"] } ] }""",
            """{ "terms": [ { "key": "term:consumer", "text": "Takes in one kind of message.", "files": ["src/AlphaConsumer.cs"] } ] }""");

        var narratives = await NarrativeWriter.WriteAsync(batch, context, claude, null, _ => { });

        Assert.Equal("Takes in one kind of message.", Assert.Single(narratives).Text);
        Assert.Contains("the definition describes the names that hold the term rather than the concept it names", claude.Requests[1].Prompt, StringComparison.Ordinal);
    }
}
