using Microsoft.EntityFrameworkCore;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Persistence;

public sealed class PosLedgerDbContext(DbContextOptions<PosLedgerDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PosLedgerDbContext).Assembly);
    }
}
