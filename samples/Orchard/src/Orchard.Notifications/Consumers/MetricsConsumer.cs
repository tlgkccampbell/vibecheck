using Orchard.Notifications.Dispatch;
using Orchard.Shared.Messaging;

namespace Orchard.Notifications.Consumers;

/// <summary>Counts the payment events by kind.</summary>
public sealed class MetricsConsumer : IConsumer<PaymentEvent>
{
    private readonly Dictionary<PaymentEventKind, int> counts = [];

    public int CountOf(PaymentEventKind kind) => counts.GetValueOrDefault(kind);

    public Task ConsumeAsync(PaymentEvent evt, CancellationToken ct)
    {
        counts[evt.Kind] = CountOf(evt.Kind) + 1;
        return Task.CompletedTask;
    }
}
