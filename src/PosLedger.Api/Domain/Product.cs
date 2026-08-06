namespace PosLedger.Api.Domain;

/// <summary>
/// A sellable item. <see cref="StockOnHand"/> is a cached projection of the movement ledger:
/// it exists so the hot path (a sale) does not have to sum the whole ledger, and
/// <c>GET /api/v1/reconciliation</c> is what proves the two never drift apart.
/// </summary>
public sealed class Product
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Business key. Unique, and what CSV imports match on.</summary>
    public required string Sku { get; set; }

    public required string Name { get; set; }

    /// <summary>Money is <c>numeric(18,2)</c>, never a float. See ADR-0003.</summary>
    public decimal UnitPrice { get; set; }

    public int StockOnHand { get; set; }

    /// <summary>
    /// Products are deactivated, never deleted: the ledger references them forever,
    /// and a sale from 2024 has to keep naming the thing that was sold.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
