using System.Text.Json;
using Vibecheck.Analysis;
using Vibecheck.Assist;
using Vibecheck.Bootstrap;
using Vibecheck.Format;
using Vibecheck.Model;
using Vibecheck.Reviewing;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Stands in for Claude: answers each request with the next canned answer, or with an answer made from the request, and records the requests.</summary>
internal sealed class FakeClaude : IClaude
{
    private readonly Queue<string> answers = new();
    private readonly Func<ClaudeRequest, string>? answer;

    /// <summary>Creates a fake that answers the requests with the answers given, in order, and fails once they run out.</summary>
    public FakeClaude(params string[] answers) => this.answers = new(answers);

    /// <summary>Creates a fake that answers each request with what a function returns for it, in any order.</summary>
    public FakeClaude(Func<ClaudeRequest, string> answer) => this.answer = answer;

    public List<ClaudeRequest> Requests { get; } = [];

    public Task<JsonElement> AskAsync(ClaudeRequest request, CancellationToken ct = default)
    {
        string text;
        lock (Requests)
        {
            Requests.Add(request);
            if (answer is not null) text = answer(request);
            else if (answers.Count == 0) throw new ClaudeException("no answer left");
            else text = answers.Dequeue();
        }
        return Task.FromResult(JsonDocument.Parse(text).RootElement.Clone());
    }
}

public class MapGeneratorTests
{
    /// <summary>Records each progress message as it is reported, on the reporting thread.</summary>
    internal sealed class Progress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }

    /// <summary>A proposal for the synthetic analysis that binds every type, with one view.</summary>
    internal const string Complete = """
        { "nodes": [
            { "id": "app", "name": "App", "purpose": "The application.", "category": "domain" },
            { "id": "app.x", "name": "Things", "purpose": "Does the thing.", "bind": ["A.X.**"] },
            { "id": "app.y", "name": "Others", "purpose": "Other work.", "bind": ["A.Y.**"] },
            { "id": "service", "name": "Service", "purpose": "The service.", "category": "integration", "bind": ["B.**"] },
            { "id": "shared", "name": "Shared", "purpose": "Results and errors.", "category": "foundation", "bind": ["Shared.**"] } ],
          "views": [ { "id": "by-concern", "name": "By concern", "purpose": "What each part is for.", "level": "root", "groups": [
            { "id": "work", "name": "Work", "leaves": ["app.x", "app.y", "service"] },
            { "id": "base", "name": "Base", "leaves": ["shared"] } ] } ] }
        """;

    /// <summary>A proposal that leaves the shared types unbound.</summary>
    private const string MissingShared = """
        { "nodes": [
            { "id": "app", "name": "App", "purpose": "The application.", "category": "domain" },
            { "id": "app.x", "name": "Things", "purpose": "Does the thing.", "bind": ["A.X.**"] },
            { "id": "app.y", "name": "Others", "purpose": "Other work.", "bind": ["A.Y.**"] },
            { "id": "service", "name": "Service", "purpose": "The service.", "bind": ["B.**"] } ],
          "views": [] }
        """;

    /// <summary>A revision of <see cref="MissingShared"/> that adds a leaf for the shared types.</summary>
    private const string AddShared = """
        { "remove": [], "nodes": [ { "id": "shared", "name": "Shared", "purpose": "Results and errors.", "category": "foundation", "bind": ["Shared.**"] } ],
          "groups": [], "moves": [] }
        """;

    /// <summary>A proposal whose identifier breaks the format.</summary>
    private const string BadIdentifier = """{ "nodes": [ { "id": "App.X", "name": "X", "purpose": "X.", "bind": ["**"] } ], "views": [] }""";

    private static ArchitectureMap Skeleton() => Bootstrap.Skeleton.Build(TestData.Analysis(), "Example.sln");

    [Fact]
    public async Task RevisesAProposalByEditingItUntilTheCheckerFindsNothing()
    {
        var claude = new FakeClaude(MissingShared, AddShared);

        var generated = await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude);

        Assert.Equal(2, claude.Requests.Count);
        Assert.Contains("Shared.Result", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("unsorted", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("""{"id":"service","name":"Service","purpose":"The service.","bind":["B.**"]}""", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("\"moves\"", claude.Requests[1].Schema, StringComparison.Ordinal);
        Assert.Equal((2, 0), (generated.Round, generated.Findings.Count));
        Assert.Equal(["Shared.**"], generated.Map.Nodes["shared"].Bind);
        Assert.Equal(Skeleton().Exclude, generated.Map.Exclude);
        Assert.Equal(Skeleton().Externals.Keys, generated.Map.Externals.Keys);
        Assert.Equal(generated.Text, MapWriter.Write(MapReader.Read(generated.Text)));
        Assert.Empty(claude.Requests[0].Tools);
    }

    [Fact]
    public async Task AsksEveryRoundAndEveryPlacementOfTheModelGiven()
    {
        var claude = new FakeClaude(MissingShared, AddShared, """{ "newLeaves": [], "placements": [] }""");

        await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude, model: "opus");
        var missing = MapGenerator.Evaluate(Skeleton(), JsonDocument.Parse(MissingShared).RootElement, TestData.Analysis()).Map!;
        await TypePlacer.PlaceAsync(missing, TestData.Analysis(), claude, model: "haiku");

        Assert.Equal(["opus", "opus", "haiku"], claude.Requests.Select(r => r.Model));
        Assert.Equal(("claude-opus-5-5", "claude-sonnet-5-5"), (ClaudeModels.Default.Map, ClaudeModels.Default.Placement));
        Assert.Equal(new ClaudeModels("sonnet", "sonnet", "sonnet", "sonnet"), ClaudeModels.All("sonnet"));
    }

    [Fact]
    public async Task SendsAnUnusableProposalBackWithItsErrors()
    {
        var claude = new FakeClaude(BadIdentifier, Complete);

        var generated = await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude);

        Assert.Contains("App.X", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(2, generated.Round);
    }

    [Fact]
    public async Task LogsEachReasonAProposalWasRejected()
    {
        var claude = new FakeClaude(BadIdentifier, Complete);
        var log = new List<string>();
        var progress = new List<string>();

        await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude, new Progress(progress), log: log.Add);

        Assert.NotEmpty(log);
        Assert.All(log, l => Assert.StartsWith("round 1 was rejected: ", l, StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("App.X", StringComparison.Ordinal));
        Assert.Contains($"round 1: the map cannot be used, for {log.Count} reason(s)", progress);
        Assert.Contains("rejected", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"moves\"", claude.Requests[1].Schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogsEachFindingAboutAUsableMap()
    {
        var claude = new FakeClaude(MissingShared, AddShared);
        var log = new List<string>();

        await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude, log: log.Add);

        var finding = Assert.Single(log);
        Assert.StartsWith("round 1 finding: 2 type(s) are unsorted", finding, StringComparison.Ordinal);
        Assert.Contains("Shared.Result", finding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsASolutionByNamespaceWhenItsUnitListingIsTooLong()
    {
        var claude = new FakeClaude(MissingShared, AddShared);
        var progress = new List<string>();

        var generated = await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude, new Progress(progress), CancellationToken.None, null, null, maxListing: 100);

        Assert.Equal((2, 0), (generated.Round, generated.Findings.Count));
        Assert.Contains("proposing a concept map from 6 types, listed by namespace", progress);
        Assert.Contains("too large to list type by type", claude.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("=== Shared ===  [2]", claude.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("=== A.X ===", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("=== Shared ===", claude.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ListsByUnitOnlyTheUnitsOfTheTypesAsked()
    {
        var analysis = TestData.Analysis();
        var units = Units.Build(analysis);

        var (byNamespace, format, listing) = MapGenerator.Listing(analysis, units, MapGenerator.MaxListing, new HashSet<string> { TestData.Service });

        Assert.False(byNamespace);
        Assert.Contains("groups the solution's types into units", format, StringComparison.Ordinal);
        Assert.Contains("Service  [2]", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("Thing", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void ListsNamespacesWithTheirLargestUnitsAndHeaviestReferences()
    {
        var analysis = TestData.Analysis();

        var listing = Units.NamespaceListing(analysis, Units.Build(analysis), names: 12, summaries: 2);

        Assert.StartsWith("6 types in 4 namespaces\n", listing, StringComparison.Ordinal);
        Assert.Contains("=== A.X ===  [2]\n  Thing\n  Thing: \"Does the thing.\"\n  -> A.Y:1, Shared:1\n", listing, StringComparison.Ordinal);
        Assert.Contains("=== Shared ===  [2]\n  <- A.X:1, B:1\n", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("Thing:", Units.NamespaceListing(analysis, Units.Build(analysis), names: 0, summaries: 0), StringComparison.Ordinal);
    }

    [Fact]
    public void EditsAMapByRemovingAddingChangingAndMovingAndKeepsItsViewWhole()
    {
        var map = MapGenerator.Evaluate(Skeleton(), JsonDocument.Parse(Complete).RootElement, TestData.Analysis()).Map!;
        var problems = new List<string>();

        var edited = MapEdits.Apply(map, JsonDocument.Parse("""
            { "remove": ["service"],
              "nodes": [ { "id": "edge", "name": "Edge", "purpose": "The outside.", "category": "integration", "bind": ["B.**"], "group": "outer" },
                         { "id": "app.x", "purpose": "Does the thing well." } ],
              "groups": [ { "view": "by-concern", "id": "outer", "name": "Outer" } ],
              "moves": [ { "type": "Shared.Error", "leaf": "edge" }, { "type": "A.X.Thing", "leaf": "app" } ] }
            """).RootElement, problems);

        Assert.False(edited.Nodes.ContainsKey("service"));
        Assert.Equal(["B.**", "Shared.Error"], edited.Nodes["edge"].Bind);
        Assert.Equal(("Things", "Does the thing well."), (edited.Nodes["app.x"].Name, edited.Nodes["app.x"].Purpose));
        Assert.Equal(["A.X.**"], edited.Nodes["app.x"].Bind);
        Assert.Equal(["app.x", "app.y"], edited.Views["by-concern"].Groups["work"].Leaves);
        Assert.Equal(["edge"], edited.Views["by-concern"].Groups["outer"].Leaves);
        Assert.Equal(["the edit moves A.X.Thing to \"app\", which is not a leaf"], problems);
        Assert.True(map.Nodes.ContainsKey("service"));

        var moved = MapEdits.Apply(edited, JsonDocument.Parse("""{ "moves": [ { "type": "Shared.Error", "leaf": "shared" } ] }""").RootElement, problems);
        Assert.Equal(["B.**"], moved.Nodes["edge"].Bind);
        Assert.Equal(["Shared.**", "Shared.Error"], moved.Nodes["shared"].Bind);
    }

    [Fact]
    public void DropsAGroupThatEditsLeaveEmpty()
    {
        var map = MapGenerator.Evaluate(Skeleton(), JsonDocument.Parse(Complete).RootElement, TestData.Analysis()).Map!;

        var edited = MapEdits.Apply(map, JsonDocument.Parse("""{ "nodes": [ { "id": "shared", "group": "work" } ] }""").RootElement, []);

        Assert.Equal(["work"], edited.Views["by-concern"].Groups.Keys);
        Assert.Equal(["app.x", "app.y", "service", "shared"], edited.Views["by-concern"].Groups["work"].Leaves);
    }

    [Fact]
    public async Task KeepsTheUsableProposalWithTheFewestFindingsWhenTheRoundsRunOut()
    {
        var claude = new FakeClaude(MissingShared, BadIdentifier, BadIdentifier);

        var generated = await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude);

        Assert.Equal(MapGenerator.MaxRounds, claude.Requests.Count);
        Assert.Equal(1, generated.Round);
        Assert.Single(generated.Findings);
    }

    [Fact]
    public async Task StopsWhenARevisionFixesNothing()
    {
        var claude = new FakeClaude(MissingShared, MissingShared, Complete);

        var generated = await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude);

        Assert.Equal(2, claude.Requests.Count);
        Assert.Equal((2, 1), (generated.Round, generated.Findings.Count));
    }

    /// <summary>The synthetic analysis, with Other referencing Thing back, so that the two form a small knot across units.</summary>
    private static AnalysisResult SmallKnot(Func<TypeInfo, TypeInfo>? adjust = null) =>
        TestData.Analysis([new(TestData.Other, TestData.Thing, "references", TestData.Thing, TestData.Y1, 3)], adjust: adjust);

    /// <summary>
    /// The synthetic analysis, with a loop of 61 types, too many for one leaf, of which K.P holds 31 and K.Q holds 30.
    /// </summary>
    private static AnalysisResult LargeKnot()
    {
        var names = Enumerable.Range(0, 61).Select(i => i < 31 ? $"K.P.T{i:00}" : $"K.Q.T{i:00}").ToList();
        return TestData.Analysis(
            names.Select((n, i) => new RawDependency(n, names[(i + 1) % names.Count], "references", names[(i + 1) % names.Count], TestData.X1, i + 1)),
            names.Select(n => new TypeInfo(n, "Example.A", [TestData.X1])));
    }

    /// <summary>A proposal for <see cref="LargeKnot"/> that leaves the shared types unbound and splits the knot between two leaves of one container.</summary>
    private const string SplitKnot = """
        { "nodes": [
            { "id": "app", "name": "App", "purpose": "The application.", "category": "domain" },
            { "id": "app.x", "name": "Things", "purpose": "Does the thing.", "bind": ["A.X.**"] },
            { "id": "app.y", "name": "Others", "purpose": "Other work.", "bind": ["A.Y.**"] },
            { "id": "service", "name": "Service", "purpose": "The service.", "bind": ["B.**"] },
            { "id": "k", "name": "K", "purpose": "The loop.", "category": "domain" },
            { "id": "k.p", "name": "P", "purpose": "One side.", "bind": ["K.P.**"] },
            { "id": "k.q", "name": "Q", "purpose": "The other side.", "bind": ["K.Q.**"] } ],
          "views": [] }
        """;

    [Fact]
    public void ExplainsACycleByTheEdgesThatBreakItAndNeverByAStepThatIsNoEdge()
    {
        // p references q and r, and each references p back, but q and r share no edge.
        var map = new ArchitectureMap { Version = 0, Solution = "Example.sln" };
        map.Nodes["p"] = new NodeSpec { Name = "P", Bind = ["A.X.**"] };
        map.Nodes["q"] = new NodeSpec { Name = "Q", Bind = ["A.Y.**"] };
        map.Nodes["r"] = new NodeSpec { Name = "R", Bind = ["B.**", "Shared.**"] };
        var analysis = TestData.Analysis([
            new(TestData.Other, TestData.Thing, "references", TestData.Thing, TestData.Y1, 3),
            new(TestData.Service, TestData.Helper, "references", TestData.Helper, TestData.B1, 6)]);

        var finding = Assert.Single(MapGenerator.Findings(Checking.Checker.Run(map, analysis)));

        Assert.StartsWith("cycle among the children of root: p, q, r\nRemoving these 2 edge(s) breaks it, lightest first:\n", finding.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 reference(s)", finding.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("q → r", finding.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsKnotsAndTellsSmallFromLarge()
    {
        var small = Assert.Single(Knots.Find(SmallKnot()));
        Assert.Equal([TestData.Thing, TestData.Other], small.Types);
        Assert.True(small.Small);

        var large = Assert.Single(Knots.Find(LargeKnot()));
        Assert.Equal(61, large.Types.Count);
        Assert.False(large.Small);
        Assert.Empty(Knots.Find(TestData.Analysis()));
    }

    [Fact]
    public async Task KeepsASmallKnotInOneLeafWithoutAskingClaudeAgain()
    {
        var claude = new FakeClaude(Complete);
        var log = new List<string>();

        var generated = await MapGenerator.GenerateAsync(Skeleton(), SmallKnot(), claude, log: log.Add);

        Assert.Single(claude.Requests);
        Assert.Contains("Bind the types of each of these sets to one leaf.\n\n- `A.X.Thing`, `A.Y.Other`\n", claude.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.Equal((1, 0), (generated.Round, generated.Findings.Count));
        Assert.Equal(["A.X.**", TestData.Other], generated.Map.Nodes["app.x"].Bind);
        Assert.Equal(["round 1 moved A.Y.Other to app.x, to keep a knot in one leaf"], log);
    }

    [Fact]
    public async Task GathersAKnotInTheLeafOfItsAttributedType()
    {
        var analysis = SmallKnot(t => t.Name == TestData.Other ? t with { Attribute = "app.y" } : t);

        var generated = await MapGenerator.GenerateAsync(Skeleton(), analysis, new FakeClaude(Complete));

        Assert.Equal(["A.Y.**", TestData.Thing], generated.Map.Nodes["app.y"].Bind);
        Assert.Empty(generated.Findings);
    }

    [Fact]
    public async Task ReportsACycleFromALargeKnotAsInTheCodeAndNeverSendsItBack()
    {
        var claude = new FakeClaude(SplitKnot, AddShared);
        var log = new List<string>();

        var generated = await MapGenerator.GenerateAsync(Skeleton(), LargeKnot(), claude, log: log.Add);

        Assert.Contains("- 61 types in `K.P` (31), `K.Q` (30)\n", claude.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("unsorted", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("cycle among", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(2, claude.Requests.Count);
        var finding = Assert.Single(generated.Findings);
        Assert.StartsWith("cycle among the children of k: k.p, k.q\nRemoving these 1 edge(s) breaks it, lightest first:\n", finding, StringComparison.Ordinal);
        Assert.Matches(@"\nk\.(p → k\.q|q → k\.p): 1 reference\(s\), from K\.[PQ]\.T\d\d → K\.[PQ]\.T\d\d \(1\)\n", finding);
        Assert.Contains(log, l => l.StartsWith("round 2 finding in the code: cycle among the children of k", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsKnotsInALongChainWithoutRunningOutOfStack()
    {
        var names = Enumerable.Range(0, 100_000).Select(i => $"C.T{i}").ToList();
        var analysis = TestData.Analysis(
            names.Select((n, i) => new RawDependency(n, names[(i + 1) % names.Count], "references", "x", TestData.X1, 1)),
            names.Select(n => new TypeInfo(n, "Example.A", [TestData.X1])));

        Assert.Equal(100_000, Assert.Single(Knots.Find(analysis)).Types.Count);
    }

    [Fact]
    public async Task KeepsTheBestProposalWhenARevisionFails()
    {
        var claude = new FakeClaude(r => r.Prompt.Contains("Shared.Result", StringComparison.Ordinal)
            ? throw new ClaudeException($"claude gave no answer within {r.Timeout!.Value.TotalSeconds:F0} s") : MissingShared);
        var progress = new List<string>();

        var generated = await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude, new Progress(progress));

        Assert.Equal((1, 1), (generated.Round, generated.Findings.Count));
        Assert.Equal([MapGenerator.RequestTimeout, MapGenerator.RequestTimeout], claude.Requests.Select(r => r.Timeout));
        Assert.Contains("round 2 failed, so the map from round 1 is kept: claude gave no answer within 900 s", progress);
    }

    [Fact]
    public async Task ReportsWhatClaudeIsDoingInEachRound()
    {
        var claude = new FakeClaude(r =>
        {
            var revising = r.Prompt.Contains("Shared.Result", StringComparison.Ordinal);
            r.Activity!(new ClaudeActivity(TimeSpan.FromSeconds(192), 9400, revising ? 2100 : 0));
            return revising ? AddShared : MissingShared;
        });
        var progress = new List<string>();

        await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude, new Progress(progress));

        Assert.Contains("proposing: thinking, about 9,400 tokens so far (3m12s)", progress);
        Assert.Contains("round 2: writing the answer, about 2,100 tokens so far after about 9,400 tokens of thinking (3m12s)", progress);
    }

    [Fact]
    public async Task FailsWhenNoProposalIsUsable()
    {
        var claude = new FakeClaude(BadIdentifier, BadIdentifier, BadIdentifier);
        await Assert.ThrowsAsync<ClaudeException>(() => MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude));
    }

    [Fact]
    public async Task PlacesUnsortedTypesInExistingAndNewLeavesByExactName()
    {
        var map = MapReader.Read(MapWriter.Write(MapGenerator.Evaluate(Skeleton(), JsonDocument.Parse(MissingShared).RootElement, TestData.Analysis()).Map!));
        var claude = new FakeClaude("""
            { "newLeaves": [ { "id": "shared", "name": "Shared", "purpose": "Results and errors." }, { "id": "unused", "name": "Unused", "purpose": "Nothing." } ],
              "placements": [ { "type": "Shared.Result", "leaf": "shared" }, { "type": "Shared.Error", "leaf": "app.x" }, { "type": "No.Such", "leaf": "shared" } ] }
            """);

        var placement = await TypePlacer.PlaceAsync(map, TestData.Analysis(), claude);

        Assert.NotNull(placement);
        Assert.Equal((2, 0), (placement.Placed, placement.Unsorted));
        Assert.Equal(["Shared.Result"], placement.Map.Nodes["shared"].Bind);
        Assert.Equal(["A.X.**", "Shared.Error"], placement.Map.Nodes["app.x"].Bind);
        Assert.False(placement.Map.Nodes.ContainsKey("unused"));
        Assert.Contains("`app.x`, Things: Does the thing.", claude.Requests[0].Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[ { "view": "by-concern", "group": "work" } ]""", "work")]
    [InlineData("[]", "edge")]
    public async Task AddsANewLeafToAGroupOfEveryViewThatCoversIt(string groups, string expected)
    {
        const string proposal = """
            { "nodes": [
                { "id": "app", "name": "App", "purpose": "The application.", "category": "domain" },
                { "id": "app.x", "name": "Things", "purpose": "Does the thing.", "bind": ["A.X.**"] },
                { "id": "app.y", "name": "Others", "purpose": "Other work.", "bind": ["A.Y.**"] },
                { "id": "service", "name": "Service", "purpose": "The service.", "bind": ["B.**"] } ],
              "views": [ { "id": "by-concern", "name": "By concern", "purpose": "What each part is for.", "level": "root", "groups": [
                { "id": "work", "name": "Work", "leaves": ["app.x", "app.y"] },
                { "id": "edge", "name": "Edge", "leaves": ["service"] } ] } ] }
            """;
        var map = MapGenerator.Evaluate(Skeleton(), JsonDocument.Parse(proposal).RootElement, TestData.Analysis()).Map!;
        var claude = new FakeClaude($$"""
            { "newLeaves": [ { "id": "shared", "name": "Shared", "purpose": "Results and errors.", "groups": {{groups}} } ],
              "placements": [ { "type": "Shared.Result", "leaf": "shared" }, { "type": "Shared.Error", "leaf": "shared" } ] }
            """);

        var placement = await TypePlacer.PlaceAsync(map, TestData.Analysis(), claude);

        Assert.Equal(2, placement!.Placed);
        Assert.Equal("shared", placement.Map.Views["by-concern"].Groups[expected].Leaves[^1]);
        Assert.Contains("`edge`, Edge: service", claude.Requests[0].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlacesNothingWhenNoTypeIsUnsorted()
    {
        var map = MapGenerator.Evaluate(Skeleton(), JsonDocument.Parse(Complete).RootElement, TestData.Analysis()).Map!;
        var claude = new FakeClaude();
        Assert.Null(await TypePlacer.PlaceAsync(map, TestData.Analysis(), claude));
        Assert.Empty(claude.Requests);
    }

    [Fact]
    public async Task DrawsAReviewWithTheGeneratedMapOnceThereIsOne()
    {
        using var repo = new TempRepo();
        using var temp = new TempDirectory();
        ReviewPipelineTests.WriteApp(repo, "class Thing { }\n");
        var before = repo.Commit("one");
        ReviewPipelineTests.WriteApp(repo, "class Thing { void Go() => Service.Run(); }\n");
        var after = repo.Commit("two");
        var cache = new ReviewCache(temp.Path, Path.Combine(temp.Path, "repo"));
        var request = new ReviewRequest(repo.Path, before, after) { Preparation = SolutionPreparation.None };

        var first = await ReviewPipeline.RunAsync(request, ReviewPipelineTests.Fake([]), cache);
        Assert.Equal("skeleton", first.Info.MapKind);

        var generated = await MapGenerator.GenerateAsync(first.Changeset.Map, first.HeadAnalysis, new FakeClaude(Complete));
        Assert.Equal(1, cache.WriteGeneratedMap(first.Target, generated.Text));
        var second = await ReviewPipeline.RunAsync(request, ReviewPipelineTests.Fake([]), cache);

        Assert.Equal(("generated", 1), (second.Info.MapKind, second.Info.MapVersion));
        Assert.Equal("added", second.Changeset.Edges.Single(e => e.From == "app.x" && e.To == "service").Status);
        Assert.Equal(2, cache.WriteGeneratedMap(first.Target, generated.Text));
        Assert.Equal(2, (await ReviewPipeline.RunAsync(request, ReviewPipelineTests.Fake([]), cache)).Info.MapVersion);
    }
}
