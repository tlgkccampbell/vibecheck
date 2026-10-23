using Orchard.Billing.Core.Invoices;
using Orchard.Billing.Core.Payments;
using Orchard.Shared.Kernel;

namespace Orchard.Billing.Core;

/// <summary>Orchestrates payment operations. The refund half lives in a second part of this type.</summary>
public sealed partial class PaymentService
{
    private readonly CapturePayment capture;
    private readonly InvoiceGenerator invoices;

    public PaymentService(CapturePayment capture, InvoiceGenerator invoices)
    {
        this.capture = capture;
        this.invoices = invoices;
    }

    public async Task<Result<string>> CaptureAsync(SessionId session, OrderId order, Money amount, CancellationToken ct)
    {
        var receipt = await capture.ExecuteAsync(session, order, amount, ct);
        if (receipt.IsFailure)
        {
            return receipt.Error!;
        }
        var invoice = invoices.Generate(order, amount);
        return $"{receipt.Value.Value}/{invoice.Number}";
    }
}
