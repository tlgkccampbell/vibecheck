using Vibecheck.Intents;
using Xunit;

namespace Vibecheck.Tests;

/// <summary>Tests of the parser of the <c>Intent</c> section of a pull request description. See <c>docs/intent.md</c> section 3.</summary>
public class IntentReaderTests
{
    private const string Description = """
        Adds refunds.

        ## Intent

        - **Refunds post as negative ledger entries.** `billing.ledger`, `billing.api → billing.ledger`
          Why: the ledger stays append-only, so the monthly
          reconciliation needs no new case.
          Instead of: a `Refund` aggregate in a new `billing.refunds` leaf.
        - **`RefundIssued` is published from the API, not the ledger.** `Orchard.Billing.Api.RefundsController`
          Why: the ledger must not reference messaging, which `notifications` owns.

          The publisher retries on its own.

        ## Testing

        - Not an intent.
        """;

    [Fact]
    public void ReadsTheClaimAnchorsWhyAlternativeAndNoteOfEachItem()
    {
        var parsed = IntentReader.Parse(Description);

        Assert.Equal(3, parsed.Line);
        Assert.Empty(parsed.Problems);
        Assert.Equal(2, parsed.Intents.Count);
        var (refunds, published) = (parsed.Intents[0], parsed.Intents[1]);

        Assert.Equal((5, "Refunds post as negative ledger entries."), (refunds.Line, refunds.Claim));
        Assert.Equal(
            [new("billing.ledger", IntentAnchorKind.Node, "billing.ledger"), new("billing.api → billing.ledger", IntentAnchorKind.Edge, "billing.api", "billing.ledger")],
            refunds.Anchors);
        Assert.Equal("the ledger stays append-only, so the monthly reconciliation needs no new case.", refunds.Why);
        Assert.Equal("a `Refund` aggregate in a new `billing.refunds` leaf.", refunds.Alternative);
        Assert.Null(refunds.Note);

        Assert.Equal("`RefundIssued` is published from the API, not the ledger.", published.Claim);
        Assert.Equal([new("Orchard.Billing.Api.RefundsController", IntentAnchorKind.Type, "Orchard.Billing.Api.RefundsController")], published.Anchors);
        Assert.Equal("the ledger must not reference messaging, which `notifications` owns.", published.Why);
        Assert.Null(published.Alternative);
        Assert.Equal("The publisher retries on its own.", published.Note);
    }

    [Theory]
    [InlineData("# Intent\n- **A.** `x`\n## Later\n- **B.** `y`\n", 2)]
    [InlineData("### intent\n- **A.** `x`\n## Later\n- **B.** `y`\n", 1)]
    [InlineData("## Intent\n- **A.** `x`\n### Detail\n- **B.** `y`\n#### Deeper\n- **C.** `z`\n## Next\n- **D.** `w`", 3)]
    [InlineData("## Intent ##\n- **A.** `x`\n", 1)]
    public void FindsTheSectionAtAnyLevelAndEndsItAtAHeadingOfItsLevelOrHigher(string text, int count)
    {
        Assert.Equal(count, IntentReader.Parse(text).Intents.Count);
    }

    [Fact]
    public void AHeadingInsideACodeBlockNeitherStartsNorEndsTheSection()
    {
        var parsed = IntentReader.Parse("```\n## Intent\n- **Not.** `x`\n```\n## Intent\n- **A.** `x`\n  ```md\n  ## Example\n  ```\n- **B.** `y`\n");

        Assert.Equal(5, parsed.Line);
        Assert.Equal(["A.", "B."], parsed.Intents.Select(i => i.Claim));
    }

    [Fact]
    public void ATextWithoutTheSectionHasNoIntents()
    {
        var parsed = IntentReader.Parse("## Intents\n- **A.** `x`\n\n## Summary\n- **B.** `y`\n");

        Assert.False(parsed.Found);
        Assert.Empty(parsed.Intents);
    }

    [Theory]
    [InlineData("- Refunds post negatively. `billing.ledger`", "Refunds post negatively.")]
    [InlineData("- Ledgers move to version 1.2 now. `billing.ledger`", "Ledgers move to version 1.2 now.")]
    [InlineData("- Is `Foo.Bar` right? `billing.ledger`", "Is `Foo.Bar` right?")]
    [InlineData("- Refunds post negatively `billing.ledger`, `src/A.cs`", "Refunds post negatively")]
    [InlineData("- Refunds use `v1.2` ledgers `billing.ledger`", "Refunds use `v1.2` ledgers")]
    [InlineData("* __Underlined claim.__ `billing.ledger`", "Underlined claim.")]
    [InlineData("1. **Numbered.** `billing.ledger`", "Numbered.")]
    public void ReadsAClaimWithoutBoldAsTheFirstSentence(string item, string claim)
    {
        var intent = Assert.Single(IntentReader.Parse("## Intent\n" + item).Intents);

        Assert.Equal(claim, intent.Claim);
        Assert.Equal("billing.ledger", intent.Anchors[0].Name);
    }

    [Fact]
    public void ReadsTheLabelsWithoutRegardToCaseOrEmphasis()
    {
        var intent = Assert.Single(IntentReader.Parse("## Intent\n- **A.** `x`\n  **WHY:** because.\n  _instead OF_: nothing.\n").Intents);

        Assert.Equal(("because.", "nothing."), (intent.Why, intent.Alternative));
    }

    [Theory]
    [InlineData("billing.api -> billing.ledger", IntentAnchorKind.Edge, "billing.api", "billing.ledger")]
    [InlineData("Orchard.A.Foo→Orchard.B.Bar", IntentAnchorKind.Edge, "Orchard.A.Foo", "Orchard.B.Bar")]
    [InlineData("billing.api → Orchard.B.Bar", IntentAnchorKind.Edge, "billing.api", "Orchard.B.Bar")]
    [InlineData("src/Orchard.Billing/Api/RefundsController.cs", IntentAnchorKind.File, "src/Orchard.Billing/Api/RefundsController.cs", null)]
    [InlineData("./docs/intent.md", IntentAnchorKind.File, "docs/intent.md", null)]
    [InlineData("Program.cs", IntentAnchorKind.File, "Program.cs", null)]
    [InlineData("json", IntentAnchorKind.Node, "json", null)]
    [InlineData("RefundsController", IntentAnchorKind.Type, "RefundsController", null)]
    [InlineData("global::Orchard.Clock", IntentAnchorKind.Type, "Orchard.Clock", null)]
    public void ReadsEachKindOfAnchor(string span, IntentAnchorKind kind, string name, string? to)
    {
        Assert.Equal(new IntentAnchor(span, kind, name, to), IntentReader.Anchor(span));
    }

    [Theory]
    [InlineData("RefundsController.Issue()")]
    [InlineData("a → b → c")]
    [InlineData("src/A.cs → billing")]
    [InlineData("two words")]
    [InlineData("List<int>")]
    [InlineData("")]
    public void ASpanThatNamesNothingIsNotAnAnchor(string span)
    {
        Assert.Null(IntentReader.Anchor(span));
    }

    [Fact]
    public void ASpanThatIsNotAnAnchorStaysInTheText()
    {
        var intent = Assert.Single(IntentReader.Parse("## Intent\n- **A.** `billing.ledger`, `Issue()`\n").Intents);

        Assert.Equal("billing.ledger", Assert.Single(intent.Anchors).Name);
        Assert.Equal("`Issue()`", intent.Note);
    }

    [Fact]
    public void KeepsAnItemWithoutAnchorsAndSkipsAnItemWithoutAClaim()
    {
        var parsed = IntentReader.Parse("## Intent\n\n- **No anchors here.**\n  Why: it is general.\n- `billing.ledger`\n  Why: no claim.\n-\n");

        var intent = Assert.Single(parsed.Intents);
        Assert.Equal(("No anchors here.", 0), (intent.Claim, intent.Anchors.Count));
        Assert.Equal([new IntentProblem(5, "the item has no claim"), new IntentProblem(7, "the item has no claim")], parsed.Problems);
    }

    [Fact]
    public void ANestedListAndAnUnindentedContinuationBelongToTheItem()
    {
        var parsed = IntentReader.Parse("## Intent\n- **A.** `x`\nWhy: lazily continued.\n  - first detail\n  - second detail\n\nA paragraph after the list.\n- **B.** `y`\n");

        Assert.Equal(["A.", "B."], parsed.Intents.Select(i => i.Claim));
        Assert.Equal("lazily continued.\n- first detail\n- second detail", parsed.Intents[0].Why);
    }

    [Fact]
    public void ReadsWindowsLineEndings()
    {
        var intent = Assert.Single(IntentReader.Parse("## Intent\r\n- **A.** `x`\r\n  Why: b.\r\n").Intents);

        Assert.Equal(("A.", "b."), (intent.Claim, intent.Why));
    }
}
