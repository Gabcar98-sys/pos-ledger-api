using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Sku).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();

        // numeric(18,2): exact decimal arithmetic. A double would round 0.1 + 0.2 and the
        // reconciliation report would never close. See docs/adr/0003-money-and-time.md.
        builder.Property(p => p.UnitPrice).HasColumnType("numeric(18,2)");

        builder.HasIndex(p => p.Sku).IsUnique();

        // The product list is ordered by sku and paginated with a keyset cursor, so the
        // unique index above is also what serves the page. No second index needed.
        // Case-insensitive search on name is served by this one:
        builder.HasIndex(p => p.Name);

        // Guards the invariant at the only level that cannot be bypassed by application code.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_products_stock_non_negative", "stock_on_hand >= 0");
            t.HasCheckConstraint("ck_products_price_non_negative", "unit_price >= 0");
        });
    }
}
