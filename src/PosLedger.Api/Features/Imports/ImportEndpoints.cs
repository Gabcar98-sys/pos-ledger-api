using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PosLedger.Api.Common;
using PosLedger.Api.Domain;
using PosLedger.Api.Features.Auth;
using PosLedger.Api.Persistence;

namespace PosLedger.Api.Features.Imports;

public static class ImportEndpoints
{
    private const long MaxUploadBytes = 5 * 1024 * 1024;
    private const int MaxSamplesPerRule = 5;

    /// <summary>Header names are matched by name, not by position, so column order does not matter.</summary>
    private static readonly string[] RequiredColumns = ["sku", "quantity"];

    public static IEndpointRouteBuilder MapImports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/imports")
            .WithTags("Imports")
            .RequireAuthorization(Roles.Admin);

        group.MapPost("/", Import)
            .DisableAntiforgery()
            .Produces<ImportReport>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Upload a stock adjustment CSV")
            .WithDescription(
                "Columns: sku, quantity, reason (optional: Import | Adjustment | Return), note (optional). "
                + "Good rows are applied and bad rows are returned grouped by the rule they broke — "
                + "rejecting the whole file over 20 bad rows out of 600 helps nobody.");

        group.MapGet("/{id:guid}", GetReport)
            .Produces<ImportReport>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Re-read the report of a past import");

        return app;
    }

    private static async Task<IResult> Import(
        IFormFile file,
        HttpContext http,
        PosLedgerDbContext db,
        CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Empty file", "The uploaded file has no content.");
        }

        if (file.Length > MaxUploadBytes)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "File too large",
                $"The file is {file.Length / 1024}KB; the limit is {MaxUploadBytes / 1024}KB.");
        }

        await using var stream = file.OpenReadStream();
        var rows = await CsvReader.ReadAsync(stream, ct);

        if (rows.Count == 0)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Empty file", "The file has no rows.");
        }

        var header = rows[0].Fields.Select(f => f.Trim().ToLowerInvariant()).ToList();
        var missing = RequiredColumns.Where(c => !header.Contains(c)).ToArray();

        if (missing.Length > 0)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "Missing columns",
                $"The header must contain: {string.Join(", ", RequiredColumns)}. Missing: {string.Join(", ", missing)}.");
        }

        var skuColumn = header.IndexOf("sku");
        var quantityColumn = header.IndexOf("quantity");
        var reasonColumn = header.IndexOf("reason");
        var noteColumn = header.IndexOf("note");

        var batch = new ImportBatch
        {
            FileName = Path.GetFileName(file.FileName),
            UploadedBy = http.User.Identity?.Name ?? "unknown"
        };

        var dataRows = rows.Skip(1).ToList();
        batch.RowsRead = dataRows.Count;

        // ── Shape and type checks, before the database is involved at all ───────────
        var candidates = new List<(CsvRow Row, string Sku, int Quantity, StockMovementReason Reason, string? Note)>();
        var seenSkus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in dataRows)
        {
            if (row.Fields.Count != header.Count)
            {
                batch.Errors.Add(Error(batch.Id, row, ImportRules.ColumnCount,
                    $"Expected {header.Count} columns, found {row.Fields.Count}."));
                continue;
            }

            var sku = row.Fields[skuColumn].Trim();
            if (sku.Length == 0)
            {
                batch.Errors.Add(Error(batch.Id, row, ImportRules.SkuRequired, "SKU is empty."));
                continue;
            }

            var quantityText = row.Fields[quantityColumn].Trim();
            if (!int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity))
            {
                batch.Errors.Add(Error(batch.Id, row, ImportRules.QuantityNotANumber,
                    $"'{quantityText}' is not a whole number."));
                continue;
            }

            if (quantity == 0)
            {
                batch.Errors.Add(Error(batch.Id, row, ImportRules.QuantityZero,
                    "Quantity is zero, so there is nothing to record."));
                continue;
            }

            var reason = StockMovementReason.Import;
            if (reasonColumn >= 0)
            {
                var reasonText = row.Fields[reasonColumn].Trim();
                if (reasonText.Length > 0)
                {
                    if (!Enum.TryParse(reasonText, ignoreCase: true, out reason)
                        || reason == StockMovementReason.Sale)
                    {
                        // Sale is excluded on purpose: sales come from the till, and letting a
                        // spreadsheet write them would put untraceable revenue in the ledger.
                        batch.Errors.Add(Error(batch.Id, row, ImportRules.ReasonInvalid,
                            $"'{reasonText}' is not one of: Import, Adjustment, Return."));
                        continue;
                    }
                }
            }

            // Checked last, after the row has been shown to be well formed. A row with a broken
            // quantity should be reported as a broken quantity, not as a duplicate of a row it
            // happens to share a SKU with — the sender can only fix what the message names.
            if (seenSkus.TryGetValue(sku, out var firstLine))
            {
                batch.Errors.Add(Error(batch.Id, row, ImportRules.SkuDuplicated,
                    $"SKU '{sku}' already appears on line {firstLine}. Combine the rows and resend."));
                continue;
            }

            seenSkus[sku] = row.LineNumber;
            candidates.Add((row, sku, quantity, reason, noteColumn >= 0 ? row.Fields[noteColumn] : null));
        }

        // ── Catalogue checks and application, under the same locks a sale uses ──────
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            var skus = candidates.Select(c => c.Sku).ToArray();

            var products = skus.Length == 0
                ? []
                : await db.Products
                    .FromSql($"SELECT * FROM products WHERE sku = ANY({skus}) ORDER BY id FOR UPDATE")
                    .ToListAsync(ct);

            var bySku = products.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            var accepted = 0;

            var errors = new List<ImportError>(batch.Errors);

            foreach (var (row, sku, quantity, reason, _) in candidates)
            {
                if (!bySku.TryGetValue(sku, out var product))
                {
                    errors.Add(Error(batch.Id, row, ImportRules.SkuUnknown, $"No product with SKU '{sku}'."));
                    continue;
                }

                if (!product.IsActive)
                {
                    errors.Add(Error(batch.Id, row, ImportRules.ProductInactive,
                        $"Product '{sku}' is inactive. Reactivate it before adjusting its stock."));
                    continue;
                }

                if (product.StockOnHand + quantity < 0)
                {
                    errors.Add(Error(batch.Id, row, ImportRules.InsufficientStock,
                        $"Removing {-quantity} would leave '{sku}' at {product.StockOnHand + quantity}."));
                    continue;
                }

                product.StockOnHand += quantity;
                product.UpdatedAt = now;

                db.StockMovements.Add(new StockMovement
                {
                    ProductId = product.Id,
                    Delta = quantity,
                    Reason = reason,
                    Reference = batch.Id.ToString("n")[..16],
                    OccurredAt = now
                });

                accepted++;
            }

            batch.Errors.Clear();
            batch.Errors.AddRange(errors);
            batch.RowsAccepted = accepted;
            batch.RowsRejected = errors.Count;

            db.ImportBatches.Add(batch);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Ok(ToReport(batch));
        });
    }

    private static async Task<IResult> GetReport(Guid id, HttpContext http, PosLedgerDbContext db, CancellationToken ct)
    {
        var batch = await db.ImportBatches.AsNoTracking()
            .Include(b => b.Errors)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        return batch is null
            ? Problem(http, StatusCodes.Status404NotFound, "Import not found", $"No import batch with id {id}.")
            : Results.Ok(ToReport(batch));
    }

    private static ImportReport ToReport(ImportBatch batch) =>
        new(batch.Id, batch.FileName, batch.RowsRead, batch.RowsAccepted, batch.RowsRejected, batch.CreatedAt,
            batch.Errors
                .GroupBy(e => e.Rule)
                .OrderByDescending(g => g.Count())
                .Select(g => new ImportErrorGroup(
                    g.Key,
                    g.Count(),
                    // A sample, not the whole list: 119 identical messages is a scroll, not a report.
                    g.OrderBy(e => e.LineNumber)
                        .Take(MaxSamplesPerRule)
                        .Select(e => new ImportErrorSample(e.LineNumber, e.Message, e.RawLine))
                        .ToList()))
                .ToList());

    private static ImportError Error(Guid batchId, CsvRow row, string rule, string message) =>
        new()
        {
            BatchId = batchId,
            LineNumber = row.LineNumber,
            Rule = rule,
            Message = message,
            RawLine = row.Raw.Length > 2000 ? row.Raw[..2000] : row.Raw
        };

    private static IResult Problem(HttpContext http, int status, string title, string detail) =>
        Results.Problem(
            title: title, detail: detail, statusCode: status, instance: http.Request.Path,
            extensions: new Dictionary<string, object?> { ["correlationId"] = http.GetCorrelationId() });
}
