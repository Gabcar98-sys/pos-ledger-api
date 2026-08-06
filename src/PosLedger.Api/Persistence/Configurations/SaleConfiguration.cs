using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Persistence.Configurations;

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public const string NumberSequence = "sale_number_seq";

    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("sales");
        builder.HasKey(s => s.Id);

        // A sequence, not MAX(number)+1: the latter is a lost-update waiting to happen the first
        // time two tills close a sale in the same millisecond. Gaps are acceptable; duplicates are not.
        builder.Property(s => s.Number)
            .HasDefaultValueSql($"nextval('{NumberSequence}')")
            .ValueGeneratedOnAdd();

        builder.HasIndex(s => s.Number).IsUnique();

        builder.Property(s => s.CashierName).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Total).HasColumnType("numeric(18,2)");

        // Sales are listed and reconciled by date; this is the index that serves both.
        builder.HasIndex(s => s.OccurredAt);

        builder.HasMany(s => s.Lines)
            .WithOne()
            .HasForeignKey(l => l.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(t => t.HasCheckConstraint("ck_sales_total_non_negative", "total >= 0"));
    }
}

public sealed class SaleLineConfiguration : IEntityTypeConfiguration<SaleLine>
{
    public void Configure(EntityTypeBuilder<SaleLine> builder)
    {
        builder.ToTable("sale_lines");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Sku).HasMaxLength(64).IsRequired();
        builder.Property(l => l.ProductName).HasMaxLength(200).IsRequired();
        builder.Property(l => l.UnitPrice).HasColumnType("numeric(18,2)");
        builder.Property(l => l.LineTotal).HasColumnType("numeric(18,2)");

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_sale_lines_quantity_positive", "quantity > 0");
            // The arithmetic is checked by the database, so a bug in the handler cannot
            // quietly produce an invoice whose lines do not add up.
            t.HasCheckConstraint("ck_sale_lines_total_matches", "line_total = unit_price * quantity");
        });
    }
}

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");

        // The key itself is the primary key: that unique index is the lock that makes
        // concurrent retries safe, so there is no point adding a surrogate id beside it.
        builder.HasKey(r => r.Key);

        builder.Property(r => r.Key).HasMaxLength(128);
        builder.Property(r => r.Endpoint).HasMaxLength(200).IsRequired();
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.ResponseBody).HasColumnType("jsonb");

        // Lets a cleanup job drop keys older than the retention window.
        builder.HasIndex(r => r.CreatedAt);
    }
}
