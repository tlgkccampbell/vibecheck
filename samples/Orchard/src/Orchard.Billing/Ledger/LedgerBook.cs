using Orchard.Shared.Kernel;

namespace Orchard.Billing.Ledger;

public enum LedgerAccount
{
    Receivable,
    Revenue,
    Cash,
}

public sealed record Posting(LedgerAccount Debit, LedgerAccount Credit, Money Amount);

/// <summary>A double-entry ledger kept in memory.</summary>
public sealed class LedgerBook
{
    private readonly List<Posting> postings = [];

    public IReadOnlyList<Posting> Postings => postings;

    public void Post(LedgerAccount debit, LedgerAccount credit, Money amount) =>
        postings.Add(new Posting(debit, credit, amount));
}
