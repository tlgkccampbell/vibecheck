using System.Text.Json;
using System.Text.Json.Serialization;
using Orchard.Shared.Kernel;

namespace Orchard.Billing.Integrations.Stripe;

/// <summary>The body sent to the provider when a charge is captured.</summary>
public sealed record ChargeRequest(Guid Order, long Amount, string Currency);

/// <summary>
/// Serializes <see cref="ChargeRequest"/> with the System.Text.Json source generator.
/// The generated part of this type, including <c>Default</c>, exists only in generated
/// code, which the sample keeps on disk under <c>Generated</c> as some repositories do.
/// </summary>
[JsonSerializable(typeof(ChargeRequest))]
internal sealed partial class StripeJsonContext : JsonSerializerContext;

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
        var body = JsonSerializer.Serialize(new ChargeRequest(order.Value, amount.Minor, amount.Currency), StripeJsonContext.Default.ChargeRequest);
        var response = await transport(body, ct);
        return string.IsNullOrEmpty(response)
            ? new Error("transient.stripe", "Stripe returned no charge id.")
            : new ChargeId(response);
    }
}
