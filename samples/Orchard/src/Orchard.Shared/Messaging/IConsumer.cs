namespace Orchard.Shared.Messaging;

/// <summary>Handles one kind of event from the bus.</summary>
public interface IConsumer<in TEvent>
    where TEvent : IEvent
{
    Task ConsumeAsync(TEvent evt, CancellationToken ct);
}
