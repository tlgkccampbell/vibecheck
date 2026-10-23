using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

public class IdentifiersTests
{
    [Theory]
    [InlineData("billing", true)]
    [InlineData("billing.core.self", true)]
    [InlineData("a-1.b2", true)]
    [InlineData("Billing", false)]
    [InlineData("billing..core", false)]
    [InlineData(".billing", false)]
    [InlineData("billing_core", false)]
    [InlineData("", false)]
    public void ValidatesIdentifiers(string id, bool valid) => Assert.Equal(valid, Identifiers.IsValid(id));

    [Fact]
    public void ParentIsIdentifierWithoutLastSegment()
    {
        Assert.Null(Identifiers.ParentOf("billing"));
        Assert.Equal("billing", Identifiers.ParentOf("billing.core"));
        Assert.Equal("root", Identifiers.ParentOrRoot("billing"));
        Assert.Equal("billing", Identifiers.TopLevelOf("billing.core.self"));
    }

    [Fact]
    public void LiftsToLowestCommonAncestor()
    {
        Assert.Equal(("root", "billing", "auth"), Identifiers.Lift("billing.core.self", "auth.sessions"));
        Assert.Equal(("billing", "billing.core", "billing.ledger"), Identifiers.Lift("billing.core.self", "billing.ledger"));
        Assert.Equal(("billing.core", "billing.core.self", "billing.core.payments"), Identifiers.Lift("billing.core.self", "billing.core.payments"));
    }

    [Theory]
    [InlineData("Example.Billing", "example-billing")]
    [InlineData("My_Project 2", "my-project-2")]
    [InlineData("--Weird--", "weird")]
    [InlineData("###", "x")]
    public void NormalizesSegments(string raw, string expected) => Assert.Equal(expected, Identifiers.NormalizeSegment(raw));

    [Fact]
    public void MatchesForbidPatterns()
    {
        Assert.True(Identifiers.PatternMatches("*", "anything"));
        Assert.True(Identifiers.PatternMatches("shared", "shared"));
        Assert.False(Identifiers.PatternMatches("shared", "shared.kernel"));
        Assert.True(Identifiers.PatternMatches("shared.*", "shared.kernel"));
        Assert.True(Identifiers.PatternMatches("shared.*", "shared"));
        Assert.False(Identifiers.PatternMatches("shared.*", "sharedx"));
    }
}
