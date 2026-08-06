using Microsoft.EntityFrameworkCore;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Persistence;

public sealed class PosLedgerDbContext(DbContextOptions<PosLedgerDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(Configurations.SaleConfiguration.NumberSequence)
            .StartsAt(1)
            .IncrementsBy(1);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PosLedgerDbContext).Assembly);
    }
}
