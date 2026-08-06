using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Persistence.Configurations;

public sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).UseIdentityAlwaysColumn();
        builder.Property(m => m.Reason).HasConversion<int>();
        builder.Property(m => m.Reference).HasMaxLength(64);

        builder.HasOne(m => m.Product)
            .WithMany()
            .HasForeignKey(m => m.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Reconciliation groups by product over a date range; this is the index that
        // turns that query from a sequential scan into an index scan.
        // Measured before/after in docs/query-optimization.md.
        builder.HasIndex(m => new { m.ProductId, m.OccurredAt });

        builder.ToTable(t =>
            t.HasCheckConstraint("ck_stock_movements_delta_not_zero", "delta <> 0"));
    }
}
