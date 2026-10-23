using Orchard.Auth.Sessions;
using Orchard.Shared.Kernel;

namespace Orchard.Auth.Tokens;

/// <summary>Issues bearer tokens for sessions.</summary>
public sealed class TokenIssuer
{
    private readonly TimeSpan lifetime;

    public TokenIssuer(TimeSpan lifetime)
    {
        this.lifetime = lifetime;
    }

    public Result<string> Issue(Session session)
    {
        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return new Error("auth.expired", "The session has expired.");
        }
        // An anonymous type: not a type of the map, and no edge may come from referencing it.
        var claims = new { Subject = session.Id.Value, Expires = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds() };
        return $"{claims.Subject:N}.{claims.Expires}";
    }
}
