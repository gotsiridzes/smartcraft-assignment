using Jobs.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobs.Core.Persistence.Configurations;

// Column names come from the snake_case naming convention. Table, key, index and constraint
// names are explicit because raw SQL and error handling depend on them.
internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
        builder.HasKey(j => j.Id).HasName("pk_jobs");

        // Ids are created in the domain. Without this, EF treats a new child entity whose key is
        // already set as Modified, issues an UPDATE that hits 0 rows, and reports a false conflict.
        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.CustomerName).HasMaxLength(Job.CustomerNameMaxLength).IsRequired();
        builder.Property(j => j.SiteAddress).HasMaxLength(Job.SiteAddressMaxLength).IsRequired();
        builder.Property(j => j.Description).HasMaxLength(Job.DescriptionMaxLength).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.Version).IsConcurrencyToken();
        builder.Ignore(j => j.Total);

        builder.HasMany(j => j.Lines)
            .WithOne()
            .HasForeignKey(l => l.JobId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_job_lines_jobs_job_id");
        builder.Navigation(j => j.Lines)
            .HasField("_lines")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne(j => j.Export)
            .WithOne()
            .HasForeignKey<ExportTask>(t => t.JobId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_export_tasks_jobs_job_id");

        builder.HasIndex(j => new { j.Status, j.CreatedAt }).HasDatabaseName("ix_jobs_status_created_at");
    }
}
