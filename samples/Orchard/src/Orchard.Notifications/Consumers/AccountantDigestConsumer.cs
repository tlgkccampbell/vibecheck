using Orchard.Notifications.Dispatch;
using Orchard.Notifications.Templates;
using Orchard.Shared.Messaging;

namespace Orchard.Notifications.Consumers;

/// <summary>Copies every refund to the accountant.</summary>
public sealed class AccountantDigestConsumer(Outbox outbox) : IConsumer<PaymentEvent>
{
    public Task ConsumeAsync(PaymentEvent evt, CancellationToken ct)
    {
        if (evt.Kind != PaymentEventKind.Refunded) return Task.CompletedTask;
        outbox.Add("accounts", new Message("Refund", $"Order {evt.OrderId.Value} refunded {evt.Amount}."));
        return Task.CompletedTask;
    }
}
