using System.Text.RegularExpressions;
using Archmap;
using Orchard.Billing.Ledger;
using Orchard.Shared.Kernel;

namespace Orchard.Billing.Core.Invoices;

/// <summary>
/// An issued invoice. It is declared beside the generator for convenience but belongs
/// to the ledger, which the attribute states; the map's patterns would otherwise put
/// it with the generator.
/// </summary>
[Archmap("billing.ledger")]
public sealed record Invoice(string Number, OrderId Order, Money Amount);

/// <summary>
/// Numbers invoices and posts them to the ledger. The regex generator gives it a
/// generated half and emits helper types into <c>System.Text.RegularExpressions.Generated</c>,
/// which the map excludes.
/// </summary>
public sealed partial class InvoiceGenerator
{
    private readonly LedgerBook ledger;
    private int next = 1;

    public InvoiceGenerator(LedgerBook ledger)
    {
        this.ledger = ledger;
    }

    [GeneratedRegex(@"^INV-\d{6}$")]
    private static partial Regex InvoiceNumber();

    public static bool IsInvoiceNumber(string text) => InvoiceNumber().IsMatch(text);

    public Invoice Generate(OrderId order, Money amount)
    {
        var invoice = new Invoice($"INV-{next++:D6}", order, amount);
        ledger.Post(LedgerAccount.Receivable, LedgerAccount.Revenue, amount);
        return invoice;
    }
}
