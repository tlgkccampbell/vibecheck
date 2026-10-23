using System.Text.Json;
using Orchard.Shared.Kernel;

namespace Orchard.Billing.Integrations.Stripe;

/// <summary>A stand-in for the Stripe SDK that serializes the charge request it would send.</summary>
public sealed class StripeClient
{
    private readonly Func<string, CancellationToken, Task<string>> transport;

    public StripeClient(Func<string, CancellationToken, Task<string>> transport)
    {
        this.transport = transport;
    }

    public async Task<Result<ChargeId>> CaptureAsync(OrderId order, Money amount, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { order = order.Value, amount = amount.Minor, currency = amount.Currency });
        var response = await transport(body, ct);
        return string.IsNullOrEmpty(response)
            ? new Error("transient.stripe", "Stripe returned no charge id.")
            : new ChargeId(response);
    }
}
