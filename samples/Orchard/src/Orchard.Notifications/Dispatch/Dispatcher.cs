using Orchard.Notifications.Templates;
using Orchard.Shared.Kernel;
using Orchard.Shared.Messaging;

namespace Orchard.Notifications.Dispatch;

public enum PaymentEventKind
{
    Captured,
    Refunded,
}

/// <summary>Raised by billing when money moves.</summary>
public sealed record PaymentEvent(PaymentEventKind Kind, OrderId OrderId, Money Amount) : IEvent;

/// <summary>Turns events into messages and hands them to the bus.</summary>
public sealed class Dispatcher
{
    private readonly EventBus bus;

    public Dispatcher(EventBus bus)
    {
        this.bus = bus;
    }

    public async Task<Result<Message>> PublishAsync(PaymentEvent evt, CancellationToken ct)
    {
        var message = PaymentTemplates.ForPayment(evt);
        var delivered = await bus.PublishAsync(evt, ct);
        return delivered.IsSuccess ? message : delivered.Error!;
    }
}
