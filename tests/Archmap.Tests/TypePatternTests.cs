using Archmap.Model;
using Xunit;

namespace Archmap.Tests;

public class TypePatternTests
{
    [Theory]
    [InlineData("Example.Billing.Core.*", "Example.Billing.Core.Service", true)]
    [InlineData("Example.Billing.Core.*", "Example.Billing.Core.Payments.Capture", false)]
    [InlineData("Example.Billing.Core.**", "Example.Billing.Core.Payments.Capture", true)]
    [InlineData("Example.Billing.Core.**", "Example.Billing.Core.Service", true)]
    [InlineData("Example.Billing.Core.**", "Example.Billing.CoreX.Service", false)]
    [InlineData("Example.Flow.Register*", "Example.Flow.RegisterFile", true)]
    [InlineData("Example.Flow.Register*", "Example.Flow.Registers.File", false)]
    [InlineData("Example.Flow.Register**", "Example.Flow.Registers.File", true)]
    [InlineData("Example.Billing.Core.Service", "Example.Billing.Core.Service", true)]
    [InlineData("Example.Billing.Core.Service", "Example.Billing.Core.ServiceX", false)]
    public void MatchesByWildcardKind(string pattern, string type, bool expected) =>
        Assert.Equal(expected, TypePattern.Parse(pattern).Matches(type, "Any"));

    [Fact]
    public void AssemblyQualifierRestrictsMatches()
    {
        var p = TypePattern.Parse("Example.Shared::Example.Shared.**");
        Assert.True(p.Matches("Example.Shared.Result", "Example.Shared"));
        Assert.False(p.Matches("Example.Shared.Result", "Example.Tests"));
        Assert.Equal("Example.Shared", p.Assembly);
        Assert.Equal("Example.Shared.**", p.Body);
    }

    [Fact]
    public void SpecificityCountsLiteralCharactersAndQualifier()
    {
        Assert.True(TypePattern.Parse("A.B.*").Specificity == TypePattern.Parse("A.B.**").Specificity);
        Assert.True(TypePattern.Parse("A.B.C.**").Specificity > TypePattern.Parse("A.B.**").Specificity);
        Assert.True(TypePattern.Parse("A.B.Service").Specificity > TypePattern.Parse("A.B.**").Specificity);
        Assert.True(TypePattern.Parse("Asm::A.B.**").Specificity > TypePattern.Parse("A.B.**").Specificity);
    }

    [Theory]
    [InlineData("", "no type pattern")]
    [InlineData("::A.B", "empty assembly qualifier")]
    [InlineData("A::B::C", "more than one assembly qualifier")]
    [InlineData("A.B/C", "character that cannot appear")]
    [InlineData("A.***", "more than two consecutive wildcards")]
    public void RejectsMalformedPatterns(string text, string fragment)
    {
        Assert.False(TypePattern.TryParse(text, out _, out var error));
        Assert.Contains(fragment, error);
    }

    [Fact]
    public void LiteralPrefixStopsAtTheFirstWildcard()
    {
        Assert.Equal("A.B", TypePattern.Parse("A.B.**").LiteralPrefix);
        Assert.Equal("A.B", TypePattern.Parse("A.B.Reg*").LiteralPrefix);
        Assert.Equal("A.B.Service", TypePattern.Parse("A.B.Service").LiteralPrefix);
    }
}
