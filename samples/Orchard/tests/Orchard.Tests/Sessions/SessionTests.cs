using Orchard.Auth.Sessions;
using Xunit;

namespace Orchard.Tests.Sessions;

public class SessionTests
{
    [Fact]
    public void Has_FindsAGrantedScope()
    {
        Assert.True(SessionBuilder.With(Scope.ReadOrders).Has(Scope.ReadOrders));
    }

    [Theory]
    [InlineData(Scope.CapturePayments)]
    [InlineData(Scope.RefundPayments)]
    public void Has_RejectsAMissingScope(Scope scope)
    {
        Assert.False(SessionBuilder.With(Scope.ReadOrders).Has(scope));
    }
}

public class ScopeChecks
{
    [Fact]
    public void AnEmptySessionHasNoScope()
    {
        var session = SessionBuilder.With();
        Assert.Empty(session.Scopes);
        Assert.False(session.Has(Scope.ReadOrders));
    }
}
