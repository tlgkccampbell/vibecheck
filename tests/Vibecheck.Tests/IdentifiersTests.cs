using Vibecheck.Model;
using Xunit;

namespace Vibecheck.Tests;

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

    [Theory]
    [InlineData("billing.core", "billing", true)]
    [InlineData("billing", "billing", true)]
    [InlineData("billing", "root", true)]
    [InlineData("billing-core", "billing", false)]
    [InlineData("auth", "billing", false)]
    public void IdentifierIsWithinItselfAndItsAncestors(string id, string ancestor, bool within) =>
        Assert.Equal(within, Identifiers.IsWithin(id, ancestor));

    [Theory]
    [InlineData("root", 0)]
    [InlineData("billing", 1)]
    [InlineData("billing.core.self", 3)]
    public void DepthCountsSegments(string id, int depth) => Assert.Equal(depth, Identifiers.Depth(id));

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
}
