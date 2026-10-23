using Orchard.Notifications.Dispatch;

namespace Orchard.Notifications.Templates;

public sealed record Message(string Subject, string Body);

public static class PaymentTemplates
{
    public static Message ForPayment(PaymentEvent evt) => evt.Kind switch
    {
        PaymentEventKind.Captured => new Message("Payment received", $"We received {evt.Amount} for order {evt.OrderId.Value}."),
        PaymentEventKind.Refunded => new Message("Refund issued", $"We refunded {evt.Amount} for order {evt.OrderId.Value}."),
        _ => throw new ArgumentOutOfRangeException(nameof(evt)),
    };
}
