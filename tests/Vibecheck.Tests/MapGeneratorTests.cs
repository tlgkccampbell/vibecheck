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

    /// <summary>A proposal whose identifier breaks the format.</summary>
    private const string BadIdentifier = """{ "nodes": [ { "id": "App.X", "name": "X", "purpose": "X.", "bind": ["**"] } ], "views": [] }""";

    private static ArchitectureMap Skeleton() => Bootstrap.Skeleton.Build(TestData.Analysis(), "Example.sln");

    [Fact]
    public async Task RevisesAProposalUntilTheCheckerFindsNothing()
    {
        var claude = new FakeClaude(MissingShared, Complete);

        var generated = await MapGenerator.GenerateAsync(Skeleton(), TestData.Analysis(), claude);

        Assert.Equal(2, claude.Requests.Count);
        Assert.Contains("Shared.Result", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("unsorted", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("\"id\": \"service\"", claude.Requests[1].Prompt, StringComparison.Ordinal);
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
        var claude = new FakeClaude(MissingShared, Complete, """{ "newLeaves": [], "placements": [] }""");

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
            return revising ? Complete : MissingShared;
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
