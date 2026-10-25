using Orchard.Notifications.Dispatch;
using Orchard.Notifications.Templates;
using Orchard.Shared.Messaging;

namespace Orchard.Notifications.Consumers;

/// <summary>Sends the customer a receipt when a payment is captured.</summary>
public sealed class ReceiptConsumer(Outbox outbox) : IConsumer<PaymentEvent>
{
    public Task ConsumeAsync(PaymentEvent evt, CancellationToken ct)
    {
        if (evt.Kind != PaymentEventKind.Captured) return Task.CompletedTask;
        outbox.Add("customer", PaymentTemplates.ForPayment(evt));
        return Task.CompletedTask;
    }
}
