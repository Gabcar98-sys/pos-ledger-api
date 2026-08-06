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

        // The reporting index, and it is a different index from the one above for a reason:
        // reconciliation filters by time across all products, so a key that starts with
        // product_id cannot serve it. The included columns let it become an index-only scan —
        // but only once VACUUM has populated the visibility map. Straight after a bulk load it
        // is still a bitmap heap scan, which is measured, with plans, in
        // docs/query-optimization.md. 37ms -> 6.5ms -> 2ms.
        builder.HasIndex(m => new { m.OccurredAt, m.Reason })
            .IncludeProperties(m => new { m.ProductId, m.Delta });

        builder.ToTable(t =>
            t.HasCheckConstraint("ck_stock_movements_delta_not_zero", "delta <> 0"));
    }
}
