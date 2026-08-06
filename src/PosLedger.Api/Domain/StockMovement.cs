namespace PosLedger.Api.Domain;

public enum StockMovementReason
{
    Sale = 1,
    Import = 2,
    Adjustment = 3,
    Return = 4
}

/// <summary>
/// Append-only ledger of every stock change. Nothing in the system updates or deletes a row here:
/// a mistake is corrected by writing the opposite movement, which is why the table can be trusted
/// as the audit trail and why reconciliation has something to compare against.
/// </summary>
public sealed class StockMovement
{
    /// <summary>bigserial — the ledger has a natural order and no need for a client-generated id.</summary>
    public long Id { get; init; }

    public Guid ProductId { get; init; }

    /// <summary>Negative for a sale, positive for a restock. Never zero.</summary>
    public int Delta { get; init; }

    public StockMovementReason Reason { get; init; }

    /// <summary>Sale number or import batch id — what caused the movement.</summary>
    public string? Reference { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public Product? Product { get; init; }
}
