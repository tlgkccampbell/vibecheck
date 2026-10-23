using Orchard.Auth.Sessions;
using Orchard.Billing.Integrations.Stripe;
using Orchard.Notifications.Dispatch;
using Orchard.Shared.Kernel;

namespace Orchard.Billing.Core.Payments;

/// <summary>Captures an authorized payment, then tells notifications about it.</summary>
public sealed class CapturePayment
{
    private readonly ISessionReader sessions;
    private readonly StripeClient stripe;
    private readonly Dispatcher dispatcher;

    public CapturePayment(ISessionReader sessions, StripeClient stripe, Dispatcher dispatcher)
    {
        this.sessions = sessions;
        this.stripe = stripe;
        this.dispatcher = dispatcher;
    }

    public async Task<Result<ChargeId>> ExecuteAsync(SessionId sessionId, OrderId order, Money amount, CancellationToken ct)
    {
        var session = await sessions.RequireScopeAsync(sessionId, Scope.CapturePayments, ct);
        if (session.IsFailure)
        {
            return session.Error!;
        }

        var charge = await stripe.CaptureAsync(order, amount, ct);
        if (charge.IsFailure)
        {
            return charge.Error!;
        }

        await dispatcher.PublishAsync(new PaymentEvent(PaymentEventKind.Captured, order, amount), ct);
        return charge.Value;
    }
}
