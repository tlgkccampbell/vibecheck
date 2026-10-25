using Orchard.Notifications.Dispatch;
using Orchard.Notifications.Templates;
using Orchard.Shared.Messaging;

namespace Orchard.Notifications.Consumers;

/// <summary>Tells the customer when a refund is issued.</summary>
public sealed class RefundNoticeConsumer(Outbox outbox) : IConsumer<PaymentEvent>
{
    public Task ConsumeAsync(PaymentEvent evt, CancellationToken ct)
    {
        if (evt.Kind != PaymentEventKind.Refunded) return Task.CompletedTask;
        outbox.Add("customer", PaymentTemplates.ForPayment(evt));
        return Task.CompletedTask;
    }
}
