using Microsoft.EntityFrameworkCore;
using Npgsql;
using PosLedger.Api.Persistence;

namespace PosLedger.Api.Features.Reconciliation;

public sealed record ProductReconciliation(
    string Sku,
    string Name,
    int UnitsSold,
    int UnitsReceived,
    int NetChange,
    int RecordedStock,
    int LedgerStock,
    int Drift);

public sealed record ReconciliationSummary(
    int ProductsWithMovement,
    int ProductsWithDrift,
    int UnitsSold,
    int UnitsReceived);

public sealed record ReconciliationReport(
    DateTimeOffset From,
    DateTimeOffset To,
    ReconciliationSummary Summary,
    IReadOnlyList<ProductReconciliation> Products);

public static class ReconciliationEndpoints
{
    /// <summary>
    /// Written as SQL rather than LINQ on purpose. It uses aggregate FILTER, which has no LINQ
    /// spelling, and it is the query whose plan is measured in docs/query-optimization.md — a
    /// query you are tuning should be a query you can read.
    /// </summary>
    private const string Sql =
        """
        WITH windowed AS (
            SELECT product_id,
                   COALESCE(SUM(-delta) FILTER (WHERE reason = 1), 0)::int AS units_sold,
                   COALESCE(SUM(delta)  FILTER (WHERE delta > 0), 0)::int  AS units_received,
                   COALESCE(SUM(delta), 0)::int                            AS net_change
            FROM stock_movements
            WHERE occurred_at >= @from AND occurred_at < @to
            GROUP BY product_id
        ),
        ledger AS (
            SELECT product_id, COALESCE(SUM(delta), 0)::int AS ledger_stock
            FROM stock_movements
            GROUP BY product_id
        )
        SELECT p.sku,
               p.name,
               COALESCE(w.units_sold, 0)      AS units_sold,
               COALESCE(w.units_received, 0)  AS units_received,
               COALESCE(w.net_change, 0)      AS net_change,
               p.stock_on_hand,
               COALESCE(l.ledger_stock, 0)    AS ledger_stock,
               p.stock_on_hand - COALESCE(l.ledger_stock, 0) AS drift
        FROM products p
        LEFT JOIN windowed w ON w.product_id = p.id
        LEFT JOIN ledger   l ON l.product_id = p.id
        WHERE w.product_id IS NOT NULL
           OR p.stock_on_hand <> COALESCE(l.ledger_stock, 0)
        ORDER BY p.sku
        """;

    public static IEndpointRouteBuilder MapReconciliation(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/reconciliation", Reconcile)
            .WithTags("Reconciliation")
            .Produces<ReconciliationReport>()
            .WithSummary("Reconcile the cached stock figure against the ledger")
            .WithDescription(
                "Lists every product that moved in the window, plus any product whose stock_on_hand "
                + "disagrees with the sum of its movements. A drift of anything other than zero means "
                + "something wrote stock without writing a movement, which is the one thing the "
                + "design forbids. Defaults to the last 30 days.");

        return app;
    }

    private static async Task<IResult> Reconcile(
        PosLedgerDbContext db,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var windowTo = to ?? DateTimeOffset.UtcNow;
        var windowFrom = from ?? windowTo.AddDays(-30);

        if (windowFrom >= windowTo)
        {
            return Results.Problem(
                title: "Invalid window",
                detail: "'from' must be earlier than 'to'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("from", windowFrom);
        command.Parameters.AddWithValue("to", windowTo);

        var products = new List<ProductReconciliation>();

        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                products.Add(new ProductReconciliation(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7)));
            }
        }

        var summary = new ReconciliationSummary(
            ProductsWithMovement: products.Count(p => p.NetChange != 0 || p.UnitsSold != 0 || p.UnitsReceived != 0),
            ProductsWithDrift: products.Count(p => p.Drift != 0),
            UnitsSold: products.Sum(p => p.UnitsSold),
            UnitsReceived: products.Sum(p => p.UnitsReceived));

        return Results.Ok(new ReconciliationReport(windowFrom, windowTo, summary, products));
    }
}
