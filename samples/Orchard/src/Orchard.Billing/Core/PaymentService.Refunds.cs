using Orchard.Shared.Kernel;

namespace Orchard.Billing.Core;

public sealed partial class PaymentService
{
    public Task<Result<string>> RefundAsync(SessionId session, OrderId order, ChargeId charge, Money amount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount.Minor);
        return Task.FromResult<Result<string>>(new Error("billing.refunds.unsupported", "Refunds are not supported yet."));
    }
}
