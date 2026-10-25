using Vibecheck.Checking;
using Vibecheck.Diffing;
using Vibecheck.Intents;
using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of how intents' anchors resolve against a changeset. See <c>docs/intent.md</c> section 4.1.</summary>
public class IntentResolverTests
{
    /// <summary>
    /// A billing system under <c>src/</c>: the API changed and gained a reference to the ledger, the ledger and the clock
    /// are untouched, and the notifications leaf was removed from the map.
    /// </summary>
    private static readonly Changeset Billing = new()
    {
        Map = new ArchitectureMap
        {
            Solution = "App.slnx",
            Nodes = new(StringComparer.Ordinal)
            {
                ["billing"] = new() { Name = "Billing" },
                ["billing.api"] = new() { Name = "API", Bind = ["App.Billing.Api.**"] },
                ["billing.ledger"] = new() { Name = "Ledger", Bind = ["App.Billing.Ledger.**"] },
                ["shared"] = new() { Name = "Shared", Bind = ["App.Shared.**"] },
            },
            Externals = new(StringComparer.Ordinal) { ["json"] = new() { Name = "System.Text.Json", Bind = ["System.Text.Json"] } },
        },
        Structural = new() { Nodes = [new("notifications", "removed", "node", null)], Views = [], Types = [] },
        Content = new(StringComparer.Ordinal),
        Files =
        [
            new("Billing/Api/RefundsController.cs", "modified", ["billing.api"], ["billing.api"]),
            new("Billing/Ledger/Ledger.cs", "unchanged", ["billing.ledger"], ["billing.ledger"]),
            new("Shared/Clock.cs", "unchanged", ["shared"], ["shared"]),
            new("Notifications/Mailer.cs", "removed", ["notifications"], []),
        ],
        Types =
        [
            new("App.Billing.Api.RefundsController", "billing.api", "billing.api", ["Billing/Api/RefundsController.cs"], "unchanged"),
            new("App.Billing.Ledger.Ledger", "billing.ledger", "billing.ledger", ["Billing/Ledger/Ledger.cs"], "unchanged"),
            new("App.Billing.Ledger.Posting<T>", "billing.ledger", "billing.ledger", ["Billing/Ledger/Ledger.cs"], "unchanged"),
            new("App.Shared.Clock", "shared", "shared", ["Shared/Clock.cs"], "unchanged"),
            new("Other::App.Shared.Clock", "shared", "shared", ["Shared/Clock.cs"], "unchanged"),
            new("App.Shared.Mailer", "shared", "shared", ["Shared/Clock.cs"], "unchanged"),
            new("App.Notifications.Mailer", "notifications", null, ["Notifications/Mailer.cs"], "removed"),
        ],
        Edges =
        [
            new("billing.api", "billing.ledger", 0, 2, "added", [new("App.Billing.Api.RefundsController", "App.Billing.Ledger.Ledger", 0, 2)]),
            new("billing.api", "shared", 1, 1, "unchanged"),
            new("billing.ledger", "json", 3, 4, "modified", [new("App.Billing.Ledger.Ledger", "System.Text.Json", 3, 4)]),
        ],
        Metrics = new(StringComparer.Ordinal),
        Levels = new(StringComparer.Ordinal),
        Views = new(StringComparer.Ordinal),
        Hunks = [],
    };

    [Theory]
    [InlineData("billing", "touched", "billing")]
    [InlineData("billing.api", "touched", "billing.api")]
    [InlineData("billing.ledger", "untouched", "billing.ledger")]
    [InlineData("json", "touched", "json")]
    [InlineData("notifications", "touched", "notifications")]
    [InlineData("billing.refunds", "unknown", null)]
    public void ANodeIsTouchedWhenItOrALeafUnderItHasAChangedFile(string node, string state, string? target)
    {
        Assert.Equal((state, target), Resolve($"`{node}`"));
    }

    [Theory]
    [InlineData("billing.api → billing.ledger", "touched", "billing.api → billing.ledger")]
    [InlineData("billing → billing.ledger", "touched", "billing → billing.ledger")]
    [InlineData("billing.api → shared", "untouched", "billing.api → shared")]
    [InlineData("billing.ledger → json", "touched", "billing.ledger → json")]
    [InlineData("billing.api → nowhere", "unknown", null)]
    public void AnEdgeBetweenBlocksIsTouchedWhenAChangedLeafEdgeRunsBetweenThem(string edge, string state, string? target)
    {
        Assert.Equal((state, target), Resolve($"`{edge}`"));
    }

    [Theory]
    [InlineData("App.Billing.Api.RefundsController → App.Billing.Ledger.Ledger", "touched")]
    [InlineData("RefundsController → Ledger", "touched")]
    [InlineData("billing.api → App.Billing.Ledger.Ledger", "touched")]
    [InlineData("App.Billing.Ledger.Ledger → App.Billing.Api.RefundsController", "untouched")]
    [InlineData("App.Billing.Api.RefundsController → App.Nowhere", "unknown")]
    public void AnEdgeBetweenTypesIsTouchedWhenTheirPairIsInTheEvidenceOfAChangedEdge(string edge, string state)
    {
        Assert.Equal(state, Resolve($"`{edge}`").State);
    }

    [Theory]
    [InlineData("src/Billing/Api/RefundsController.cs", "touched", "Billing/Api/RefundsController.cs")]
    [InlineData("src/Billing/Ledger/Ledger.cs", "untouched", "Billing/Ledger/Ledger.cs")]
    [InlineData("src/Notifications/Mailer.cs", "touched", "Notifications/Mailer.cs")]
    [InlineData("src/Billing/Absent.cs", "unknown", null)]
    [InlineData("Billing/Api/RefundsController.cs", "unknown", null)]
    public void AFileIsGivenFromTheRepositorysTopAndTouchedWhenItChanged(string file, string state, string? target)
    {
        Assert.Equal((state, target), Resolve($"`{file}`"));
    }

    [Theory]
    [InlineData("App.Billing.Api.RefundsController", "touched", "App.Billing.Api.RefundsController")]
    [InlineData("RefundsController", "touched", "App.Billing.Api.RefundsController")]
    [InlineData("App.Billing.Ledger.Posting", "untouched", "App.Billing.Ledger.Posting<T>")]
    [InlineData("App.Shared.Clock", "untouched", "App.Shared.Clock")]
    [InlineData("App.Notifications.Mailer", "touched", "App.Notifications.Mailer")]
    [InlineData("Mailer", "unknown", null)]
    [InlineData("App.Nowhere", "unknown", null)]
    public void ATypeIsTouchedWhenAFileThatDeclaresItChanged(string type, string state, string? target)
    {
        Assert.Equal((state, target), Resolve($"`{type}`"));
    }

    [Fact]
    public void UnderAMapThatIsNotTheRepositorysANodeIsUnknownAndFilesAndTypesStillResolve()
    {
        var intent = Assert.Single(IntentResolver.Resolve(Parse("`billing.api`, `billing.api → billing.ledger`, `src/Billing/Api/RefundsController.cs`, `RefundsController`"), Billing, "src", ownMap: false));

        Assert.Equal(["unknown", "unknown", "touched", "touched"], intent.Anchors.Select(a => a.State));
    }

    [Fact]
    public void AnIntentWithNoTouchedAnchorIsUnbacked()
    {
        var intents = IntentResolver.Resolve(IntentReader.Parse("""
            ## Intent
            - **A.** `billing.ledger`, `billing.refunds`
            - **B.** `billing.ledger`, `billing.api`
            - **C.**
            """).Intents, Billing, "src", ownMap: true);

        Assert.Equal([true, false, false], intents.Select(i => i.Unbacked));
    }

    [Fact]
    public void AResolvedIntentKeepsItsParts()
    {
        var intent = Assert.Single(IntentResolver.Resolve(IntentReader.Parse("## Intent\n- **A.** `billing`\n  Why: b.\n  Instead of: c.\n  More.\n").Intents, Billing, "src", ownMap: true));

        Assert.Equal((2, "A.", "b.", "c. More.", (string?)null), (intent.Line, intent.Claim, intent.Why, intent.Alternative, intent.Note));
        Assert.Equal(new ResolvedAnchor("billing", "node", "touched") { Target = "billing" }, intent.Anchors[0]);
    }

    [Fact]
    public void TheIntentsSurviveTheChangesetsJson()
    {
        var cs = Billing with { Intents = IntentResolver.Resolve(Parse("`billing.ledger`"), Billing, "src", ownMap: true) };

        var json = FactsJson.Serialize(cs);
        var back = FactsJson.Deserialize<Changeset>(json);

        Assert.Contains("\"unbacked\":true", json.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(cs.Intents[0].Anchors, back.Intents[0].Anchors);
    }

    private static IReadOnlyList<Intent> Parse(string anchors) => IntentReader.Parse($"## Intent\n- **A.** {anchors}\n").Intents;

    private static (string State, string? Target) Resolve(string anchor)
    {
        var resolved = Assert.Single(Assert.Single(IntentResolver.Resolve(Parse(anchor), Billing, "src", ownMap: true)).Anchors);
        return (resolved.State, resolved.Target);
    }
}
