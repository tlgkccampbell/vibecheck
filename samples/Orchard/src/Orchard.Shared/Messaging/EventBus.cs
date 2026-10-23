using Orchard.Shared.Kernel;

namespace Orchard.Shared.Messaging;

/// <summary>A marker for events published on the bus.</summary>
public interface IEvent
{
    OrderId OrderId { get; }
}

/// <summary>An in-process event bus.</summary>
public sealed class EventBus
{
    private readonly List<Func<IEvent, CancellationToken, Task>> handlers = [];

    public void Subscribe(Func<IEvent, CancellationToken, Task> handler) => handlers.Add(handler);

    public async Task<Result<int>> PublishAsync(IEvent evt, CancellationToken ct)
    {
        var delivered = 0;
        foreach (var handler in handlers)
        {
            await handler(evt, ct);
            delivered++;
        }
        return delivered;
    }
}
