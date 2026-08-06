namespace PosLedger.Api.Common;

/// <summary>
/// Keyset ("seek") page. There is no total count and no page number on purpose: <c>OFFSET</c> gets
/// slower the deeper you go and skips or repeats rows when the underlying data changes between
/// requests, which is exactly what happens on a product list that is being imported into.
/// The cursor is the sort key of the last row returned. See ADR-0004.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public bool HasMore => NextCursor is not null;
}
