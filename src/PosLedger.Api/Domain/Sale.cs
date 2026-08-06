namespace PosLedger.Api.Domain;

/// <summary>
/// A completed sale. Immutable once written: a mistake is corrected with a return movement,
/// never by editing the sale, because the sale is what was handed to the customer on paper.
/// </summary>
public sealed class Sale
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Human-facing consecutive number, assigned by a Postgres sequence.</summary>
    public long Number { get; init; }

    public required string CashierName { get; init; }

    public decimal Total { get; set; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public List<SaleLine> Lines { get; init; } = [];
}

/// <summary>
/// One line of a sale. Sku, name and unit price are <b>copied</b> from the product rather than
/// referenced: renaming a product or repricing it must not rewrite last month's invoices.
/// The product id stays for traceability.
/// </summary>
public sealed class SaleLine
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SaleId { get; init; }

    public Guid ProductId { get; init; }

    public required string Sku { get; init; }

    public required string ProductName { get; init; }

    public int Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    /// <summary>Stored, not computed on read: it is part of what the customer was charged.</summary>
    public decimal LineTotal { get; init; }
}
