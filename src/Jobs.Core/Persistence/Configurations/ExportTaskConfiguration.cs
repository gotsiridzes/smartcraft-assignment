using Jobs.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobs.Core.Persistence.Configurations;

internal sealed class ExportTaskConfiguration : IEntityTypeConfiguration<ExportTask>
{
    public void Configure(EntityTypeBuilder<ExportTask> builder)
    {
        builder.ToTable("export_tasks");
        builder.HasKey(t => t.Id).HasName("pk_export_tasks");
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(t => t.LockedBy).HasMaxLength(ExportTask.LockedByMaxLength);
        builder.Property(t => t.LastError).HasMaxLength(ExportTask.LastErrorMaxLength);
        builder.Property(t => t.ExternalInvoiceId).HasMaxLength(ExportTask.ExternalInvoiceIdMaxLength);

        // One export per job: the database guarantee behind "never two invoices".
        builder.HasIndex(t => t.JobId).IsUnique().HasDatabaseName(DatabaseConstraints.ExportTaskUniqueJob);
        builder.HasIndex(t => new { t.Status, t.NextAttemptAt }).HasDatabaseName("ix_export_tasks_status_next_attempt_at");
    }
}
