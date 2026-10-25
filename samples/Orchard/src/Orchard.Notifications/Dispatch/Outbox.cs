using Orchard.Notifications.Templates;

namespace Orchard.Notifications.Dispatch;

/// <summary>Holds the messages waiting to be sent.</summary>
public sealed class Outbox
{
    private readonly List<(string To, Message Message)> pending = [];

    public IReadOnlyList<(string To, Message Message)> Pending => pending;

    public void Add(string to, Message message) => pending.Add((to, message));
}
