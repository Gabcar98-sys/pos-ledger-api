using Microsoft.EntityFrameworkCore;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Persistence;

/// <summary>
/// Applies migrations (and optionally a demo catalogue) at startup.
/// <para>
/// Running migrations from the app is off by default because in a real deployment it is wrong:
/// two instances rolling at once both try to migrate, and a failed migration takes the app down
/// instead of failing a pipeline step. It is switched on for docker compose and for the hosted
/// demo, where there is exactly one instance and no release pipeline to hang it off.
/// See docs/adr/0005-migrations-on-startup.md.
/// </para>
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Database");
        var db = scope.ServiceProvider.GetRequiredService<PosLedgerDbContext>();

        if (configuration.GetValue("Database:MigrateOnStartup", false))
        {
            logger.LogInformation("Applying migrations");
            await db.Database.MigrateAsync(ct);
        }

        if (configuration.GetValue("Database:SeedOnStartup", false))
        {
            await SeedAsync(db, logger, ct);
        }
    }

    private static async Task SeedAsync(PosLedgerDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(ct))
        {
            return;
        }

        logger.LogInformation("Seeding demo catalogue");

        // A small catalogue so that a fresh `docker compose up` has something to sell.
        var catalogue = new (string Sku, string Name, decimal Price, int Stock)[]
        {
            ("CAF-500", "Ground coffee 500g", 28500m, 40),
            ("CAF-1000", "Ground coffee 1kg", 52000m, 25),
            ("TEA-020", "Black tea, 20 bags", 12900m, 60),
            ("MUG-STD", "Ceramic mug", 18000m, 15),
            ("FLT-100", "Paper filters, 100", 9500m, 80),
            ("GRD-MAN", "Manual grinder", 145000m, 6)
        };

        foreach (var (sku, name, price, stock) in catalogue)
        {
            var product = new Product { Sku = sku, Name = name, UnitPrice = price, StockOnHand = stock };
            db.Products.Add(product);
            db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                Delta = stock,
                Reason = StockMovementReason.Adjustment,
                Reference = "opening-balance"
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
