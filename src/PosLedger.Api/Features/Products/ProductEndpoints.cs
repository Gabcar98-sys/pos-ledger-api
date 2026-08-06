using Microsoft.EntityFrameworkCore;
using Npgsql;
using PosLedger.Api.Common;
using PosLedger.Api.Domain;
using PosLedger.Api.Persistence;

namespace PosLedger.Api.Features.Products;

public static class ProductEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapProducts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/products")
            .WithTags("Products");

        group.MapPost("/", Create)
            .Validate<CreateProductRequest>()
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Create a product")
            .WithDescription("Initial stock, if any, is written to the ledger as an Adjustment movement.");

        group.MapGet("/", List)
            .Produces<Page<ProductResponse>>()
            .WithSummary("List products (keyset pagination)");

        group.MapGet("/{id:guid}", GetById)
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Get a product by id");

        group.MapPut("/{id:guid}", Update)
            .Validate<UpdateProductRequest>()
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Update name, price or active flag")
            .WithDescription("Stock is never set here — it only moves through the ledger.");

        return app;
    }

    private static async Task<IResult> Create(
        CreateProductRequest request,
        PosLedgerDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var product = new Product
        {
            Sku = request.Sku,
            Name = request.Name,
            UnitPrice = request.UnitPrice,
            StockOnHand = request.InitialStock
        };

        // The product row and its opening ledger entry are one unit of work: a product whose
        // stock does not trace back to a movement would break reconciliation from day one.
        // No explicit transaction needed — a single SaveChanges is already atomic.
        db.Products.Add(product);

        if (request.InitialStock > 0)
        {
            db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                Delta = request.InitialStock,
                Reason = StockMovementReason.Adjustment,
                Reference = "opening-balance"
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Unique violation on sku. Checking first with a SELECT would still race;
            // the constraint is the only place the answer is authoritative.
            return Problem(http, StatusCodes.Status409Conflict, "SKU already exists",
                $"A product with SKU '{request.Sku}' already exists.");
        }

        return TypedResults.Created($"/api/v1/products/{product.Id}", ProductResponse.From(product));
    }

    private static async Task<IResult> List(
        PosLedgerDbContext db,
        CancellationToken ct,
        string? q = null,
        string? after = null,
        int? limit = null,
        bool includeInactive = false)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var query = db.Products.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // ILIKE is Postgres' case-insensitive match; EF.Functions.ILike keeps it in SQL
            // instead of pulling the table into memory to run a C# Contains.
            var pattern = $"%{q.Trim()}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Sku, pattern));
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            query = query.Where(p => string.Compare(p.Sku, after) > 0);
        }

        // Fetch one extra row to know whether there is a next page without a second count query.
        var rows = await query
            .OrderBy(p => p.Sku)
            .Take(take + 1)
            // Projected inline rather than through ProductResponse.From: EF has to translate this
            // to a column list, and it cannot see inside a static method.
            .Select(p => new ProductResponse(
                p.Id, p.Sku, p.Name, p.UnitPrice, p.StockOnHand, p.IsActive, p.CreatedAt, p.UpdatedAt))
            .ToListAsync(ct);

        var hasMore = rows.Count > take;
        var items = hasMore ? rows[..take] : rows;

        return TypedResults.Ok(new Page<ProductResponse>(items, hasMore ? items[^1].Sku : null));
    }

    private static async Task<IResult> GetById(
        Guid id,
        PosLedgerDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

        return product is null
            ? Problem(http, StatusCodes.Status404NotFound, "Product not found", $"No product with id {id}.")
            : TypedResults.Ok(ProductResponse.From(product));
    }

    private static async Task<IResult> Update(
        Guid id,
        UpdateProductRequest request,
        PosLedgerDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return Problem(http, StatusCodes.Status404NotFound, "Product not found", $"No product with id {id}.");
        }

        product.Name = request.Name;
        product.UnitPrice = request.UnitPrice;
        product.IsActive = request.IsActive;
        product.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(ProductResponse.From(product));
    }

    private static IResult Problem(HttpContext http, int status, string title, string detail) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: status,
            instance: http.Request.Path,
            extensions: new Dictionary<string, object?> { ["correlationId"] = http.GetCorrelationId() });
}
