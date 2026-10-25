using Orchard.Auth.Sessions;
using Orchard.Shared.Kernel;

namespace Orchard.Tests.Sessions;

/// <summary>Builds sessions for the tests.</summary>
public static class SessionBuilder
{
    public static Session With(params Scope[] scopes) =>
        new(new SessionId(Guid.NewGuid()), "subject", scopes.ToHashSet(), DateTimeOffset.UtcNow.AddHours(1));
}
