using Orchard.Billing.Core;
using Orchard.Shared.Kernel;

namespace Orchard.Billing.Api;

/// <summary>The HTTP-facing surface of billing, reduced to a method per endpoint.</summary>
public sealed class BillingEndpoints
{
    private readonly PaymentService payments;

    public BillingEndpoints(PaymentService payments)
    {
        this.payments = payments;
    }

    public Task<Result<string>> PostCaptureAsync(SessionId session, OrderId order, Money amount, CancellationToken ct) =>
        payments.CaptureAsync(session, order, amount, ct);

    public Task<Result<string>> PostRefundAsync(SessionId session, OrderId order, ChargeId charge, Money amount, CancellationToken ct) =>
        payments.RefundAsync(session, order, charge, amount, ct);
}
