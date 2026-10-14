using Jobs.Core.Persistence.FakeAccounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobs.Core.Persistence.Configurations;

internal sealed class FakeInvoiceConfiguration : IEntityTypeConfiguration<FakeInvoice>
{
    public void Configure(EntityTypeBuilder<FakeInvoice> builder)
    {
        builder.ToTable("invoices", "fake_accounting");
        builder.HasKey(i => i.Id).HasName("pk_invoices");
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.IdempotencyKey).HasMaxLength(FakeInvoice.IdempotencyKeyMaxLength).IsRequired();
        builder.Property(i => i.Total).HasPrecision(18, 2);

        // Receiver-side dedup: the same key always maps to the same invoice.
        builder.HasIndex(i => i.IdempotencyKey).IsUnique().HasDatabaseName("ix_invoices_idempotency_key");
    }
}
