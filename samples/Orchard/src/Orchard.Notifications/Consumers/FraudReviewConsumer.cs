using Orchard.Notifications.Dispatch;
using Orchard.Notifications.Templates;
using Orchard.Shared.Messaging;

namespace Orchard.Notifications.Consumers;

/// <summary>Asks the risk team to review a large capture.</summary>
public sealed class FraudReviewConsumer(Outbox outbox) : IConsumer<PaymentEvent>
{
    public Task ConsumeAsync(PaymentEvent evt, CancellationToken ct)
    {
        if (evt.Kind != PaymentEventKind.Captured || evt.Amount.Minor < 100_000) return Task.CompletedTask;
        outbox.Add("risk", new Message("Review payment", $"Order {evt.OrderId.Value} paid {evt.Amount}."));
        return Task.CompletedTask;
    }
}
