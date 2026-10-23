using Orchard.Shared.Kernel;

namespace Orchard.Auth.Sessions;

/// <summary>Looks up sessions and checks their scopes.</summary>
public interface ISessionReader
{
    Task<Result<Session>> RequireAsync(SessionId id, CancellationToken ct);

    /// <summary>Returns the session when it exists and carries the scope, and an error otherwise.</summary>
    Task<Result<Session>> RequireScopeAsync(SessionId id, Scope scope, CancellationToken ct);
}
