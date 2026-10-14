using Jobs.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobs.Core.Persistence.Configurations;

internal sealed class JobLineConfiguration : IEntityTypeConfiguration<JobLine>
{
    public void Configure(EntityTypeBuilder<JobLine> builder)
    {
        builder.ToTable("job_lines", table =>
        {
            table.HasCheckConstraint("ck_job_lines_quantity_positive", "quantity > 0");
            table.HasCheckConstraint("ck_job_lines_unit_price_non_negative", "unit_price >= 0");
        });
        builder.HasKey(l => l.Id).HasName("pk_job_lines");
        builder.Property(l => l.Id).ValueGeneratedNever();

        builder.Property(l => l.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(JobLine.DescriptionMaxLength).IsRequired();
        builder.Property(l => l.Quantity).HasPrecision(18, 3);
        builder.Property(l => l.UnitPrice).HasPrecision(18, 2);
        builder.Ignore(l => l.Total);

        builder.HasIndex(l => l.JobId).HasDatabaseName("ix_job_lines_job_id");
    }
}
