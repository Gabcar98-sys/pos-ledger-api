namespace PosLedger.Api.Domain;

/// <summary>
/// Remembers the outcome of a request that carried an <c>Idempotency-Key</c>.
/// <para>
/// A till retries. The network drops the response, the cashier presses the button again, and
/// without this the customer is charged twice and the stock is decremented twice. The key is the
/// primary key of this table, so the second attempt collides on the unique index rather than on
/// a check-then-insert race that a busy till would eventually lose.
/// </para>
/// </summary>
public sealed class IdempotencyRecord
{
    public required string Key { get; init; }

    public required string Endpoint { get; init; }

    /// <summary>
    /// Hash of the request body. Reusing one key for a different payload is a client bug, and
    /// answering it with the first response would hide it — so it is rejected instead.
    /// </summary>
    public required string RequestHash { get; init; }

    public int ResponseStatusCode { get; set; }

    public string? ResponseBody { get; set; }

    public Guid? SaleId { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
