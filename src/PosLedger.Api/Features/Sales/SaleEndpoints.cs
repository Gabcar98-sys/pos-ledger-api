using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PosLedger.Api.Common;
using PosLedger.Api.Domain;
using PosLedger.Api.Features.Auth;
using PosLedger.Api.Persistence;

namespace PosLedger.Api.Features.Sales;

public static class SaleEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string ReplayHeader = "Idempotent-Replay";
    private const string Endpoint = "POST /api/v1/sales";

    /// <summary>
    /// The stored idempotent response is served back verbatim, so it has to be serialised with the
    /// same conventions the framework uses — otherwise a replay answers in a different shape than
    /// the original call.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSales(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sales")
            .WithTags("Sales")
            .RequireAuthorization();

        group.MapPost("/", Create)
            .Validate<CreateSaleRequest>()
            .Produces<SaleResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Register a sale")
            .WithDescription(
                "Requires an Idempotency-Key header. Replaying the same key returns the original "
                + "response instead of charging twice. Stock is decremented under a row lock, so a "
                + "sale that would take stock below zero is rejected rather than queued.");

        group.MapGet("/{id:guid}", GetById)
            .Produces<SaleResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Get a sale by id");

        group.MapGet("/", List)
            .Produces<IReadOnlyList<SaleResponse>>()
            .WithSummary("List recent sales, most recent first");

        return app;
    }

    private static async Task<IResult> Create(
        CreateSaleRequest request,
        HttpContext http,
        PosLedgerDbContext db,
        CancellationToken ct)
    {
        var key = http.Request.Headers[IdempotencyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Missing Idempotency-Key",
                $"A sale must carry an {IdempotencyHeader} header of at most 128 characters. "
                + "A till retries, and without a key a retry charges the customer twice.");
        }

        var cashier = http.User.Identity?.Name ?? "unknown";
        var requestHash = HashRequest(request);

        // EnableRetryOnFailure means EF may re-run this whole block after a transient failure,
        // so it has to be re-runnable: nothing is tracked before the strategy starts, and the
        // change tracker is cleared on entry.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction =
                await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            // ── 1. Claim the idempotency key ───────────────────────────────────────────
            // A concurrent duplicate blocks on this unique index until we commit or roll back,
            // and then gets the conflict below. That is the whole mechanism: no check-then-act
            // window, because the index entry is the claim.
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key,
                Endpoint = Endpoint,
                RequestHash = requestHash
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                await transaction.RollbackAsync(ct);
                return await ReplayAsync(key, requestHash, http, db, ct);
            }

            // ── 2. Lock the products ───────────────────────────────────────────────────
            var productIds = request.Lines.Select(l => l.ProductId).Distinct().OrderBy(id => id).ToArray();

            // FOR UPDATE holds the rows until this transaction ends, so no other sale can read a
            // stock figure we are about to change. ORDER BY id makes every transaction take the
            // locks in the same order, which is what stops two overlapping sales deadlocking.
            var products = await db.Products
                .FromSql($"SELECT * FROM products WHERE id = ANY({productIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct);

            var byId = products.ToDictionary(p => p.Id);

            var missing = productIds.Where(id => !byId.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                await transaction.RollbackAsync(ct);
                return Problem(http, StatusCodes.Status422UnprocessableEntity, "Unknown product",
                    $"No product with id {string.Join(", ", missing)}.");
            }

            var inactive = products.Where(p => !p.IsActive).Select(p => p.Sku).ToArray();
            if (inactive.Length > 0)
            {
                await transaction.RollbackAsync(ct);
                return Problem(http, StatusCodes.Status422UnprocessableEntity, "Product not for sale",
                    $"These products are inactive: {string.Join(", ", inactive)}.");
            }

            var shortfalls = request.Lines
                .Where(line => byId[line.ProductId].StockOnHand < line.Quantity)
                .Select(line => $"{byId[line.ProductId].Sku} (requested {line.Quantity}, available {byId[line.ProductId].StockOnHand})")
                .ToArray();

            if (shortfalls.Length > 0)
            {
                await transaction.RollbackAsync(ct);
                return Problem(http, StatusCodes.Status409Conflict, "Insufficient stock",
                    $"Not enough stock for: {string.Join("; ", shortfalls)}.");
            }

            // ── 3. Write the sale, the lines and the ledger ────────────────────────────
            var sale = new Sale { CashierName = cashier };
            var now = DateTimeOffset.UtcNow;

            foreach (var line in request.Lines)
            {
                var product = byId[line.ProductId];

                sale.Lines.Add(new SaleLine
                {
                    SaleId = sale.Id,
                    ProductId = product.Id,
                    // Copied, not referenced: this invoice must still read the same after a rename.
                    Sku = product.Sku,
                    ProductName = product.Name,
                    Quantity = line.Quantity,
                    UnitPrice = product.UnitPrice,
                    LineTotal = product.UnitPrice * line.Quantity
                });

                product.StockOnHand -= line.Quantity;
                product.UpdatedAt = now;

                db.StockMovements.Add(new StockMovement
                {
                    ProductId = product.Id,
                    Delta = -line.Quantity,
                    Reason = StockMovementReason.Sale,
                    Reference = sale.Id.ToString("n")[..16],
                    OccurredAt = now
                });
            }

            sale.Total = sale.Lines.Sum(l => l.LineTotal);
            db.Sales.Add(sale);

            await db.SaveChangesAsync(ct);

            // ── 4. Remember the response for the retry that is coming ──────────────────
            var response = SaleResponse.From(sale);
            var body = JsonSerializer.Serialize(response, ResponseJson);

            var record = await db.IdempotencyRecords.FirstAsync(r => r.Key == key, ct);
            record.ResponseStatusCode = StatusCodes.Status201Created;
            record.ResponseBody = body;
            record.SaleId = sale.Id;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            http.Response.Headers.Location = $"/api/v1/sales/{sale.Id}";
            return Results.Content(body, "application/json", Encoding.UTF8, StatusCodes.Status201Created);
        });
    }

    /// <summary>
    /// The key was already used. If the body matches, hand back exactly what the first call
    /// returned; if it does not, the client has reused a key for different work, which is a bug
    /// worth surfacing rather than papering over.
    /// </summary>
    private static async Task<IResult> ReplayAsync(
        string key, string requestHash, HttpContext http, PosLedgerDbContext db, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);

        if (existing is null)
        {
            // The holder rolled back after we collided with it, so there is nothing to replay.
            return Problem(http, StatusCodes.Status409Conflict, "Request in flight",
                "Another request with this Idempotency-Key is still being processed. Retry shortly.");
        }

        if (existing.RequestHash != requestHash)
        {
            return Problem(http, StatusCodes.Status422UnprocessableEntity, "Idempotency-Key reused",
                "This Idempotency-Key was already used for a different request body.");
        }

        if (existing.ResponseBody is null)
        {
            return Problem(http, StatusCodes.Status409Conflict, "Request in flight",
                "Another request with this Idempotency-Key is still being processed. Retry shortly.");
        }

        http.Response.Headers[ReplayHeader] = "true";
        http.Response.Headers.Location = $"/api/v1/sales/{existing.SaleId}";

        return Results.Content(existing.ResponseBody, "application/json", Encoding.UTF8, existing.ResponseStatusCode);
    }

    private static async Task<IResult> GetById(Guid id, HttpContext http, PosLedgerDbContext db, CancellationToken ct)
    {
        var sale = await db.Sales.AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        return sale is null
            ? Problem(http, StatusCodes.Status404NotFound, "Sale not found", $"No sale with id {id}.")
            : Results.Ok(SaleResponse.From(sale));
    }

    private static async Task<IResult> List(
        PosLedgerDbContext db,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 50)
    {
        var take = Math.Clamp(limit, 1, 200);

        var query = db.Sales.AsNoTracking().Include(s => s.Lines).AsQueryable();

        if (from is not null)
        {
            query = query.Where(s => s.OccurredAt >= from);
        }

        if (to is not null)
        {
            query = query.Where(s => s.OccurredAt < to);
        }

        var sales = await query.OrderByDescending(s => s.Number).Take(take).ToListAsync(ct);

        return Results.Ok(sales.Select(SaleResponse.From).ToList());
    }

    /// <summary>
    /// Fingerprint of what was asked for, insensitive to line order — the same basket scanned in a
    /// different order is the same basket.
    /// </summary>
    private static string HashRequest(CreateSaleRequest request)
    {
        var normalised = string.Join('|', request.Lines
            .OrderBy(l => l.ProductId)
            .Select(l => $"{l.ProductId:n}:{l.Quantity}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised))).ToLowerInvariant();
    }

    private static IResult Problem(HttpContext http, int status, string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: status,
            instance: http.Request.Path,
            extensions: new Dictionary<string, object?> { ["correlationId"] = http.GetCorrelationId() });
}
