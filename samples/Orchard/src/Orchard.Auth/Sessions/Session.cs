using Orchard.Shared.Kernel;

namespace Orchard.Auth.Sessions;

public enum Scope
{
    ReadOrders,
    CapturePayments,
    RefundPayments,
}

/// <summary>An authenticated session and the scopes it carries.</summary>
public sealed record Session(SessionId Id, string Subject, IReadOnlySet<Scope> Scopes, DateTimeOffset ExpiresAt)
{
    public bool Has(Scope scope) => Scopes.Contains(scope);
}
