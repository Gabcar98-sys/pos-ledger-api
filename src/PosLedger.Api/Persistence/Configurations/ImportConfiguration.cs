using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Persistence.Configurations;

public sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        builder.ToTable("import_batches");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.FileName).HasMaxLength(260).IsRequired();
        builder.Property(b => b.UploadedBy).HasMaxLength(100).IsRequired();

        builder.HasMany(b => b.Errors)
            .WithOne()
            .HasForeignKey(e => e.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(b => b.CreatedAt);
    }
}

public sealed class ImportErrorConfiguration : IEntityTypeConfiguration<ImportError>
{
    public void Configure(EntityTypeBuilder<ImportError> builder)
    {
        builder.ToTable("import_errors");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.Property(e => e.Rule).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Message).HasMaxLength(500).IsRequired();
        builder.Property(e => e.RawLine).HasMaxLength(2000).IsRequired();

        // The report is read grouped by rule, so that is how it is indexed.
        builder.HasIndex(e => new { e.BatchId, e.Rule });
    }
}
