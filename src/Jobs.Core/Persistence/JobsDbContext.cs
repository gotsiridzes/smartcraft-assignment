using Jobs.Core.Domain;
using Jobs.Core.Persistence.FakeAccounting;
using Microsoft.EntityFrameworkCore;

namespace Jobs.Core.Persistence;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobLine> JobLines => Set<JobLine>();

    public DbSet<ExportTask> ExportTasks => Set<ExportTask>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    public DbSet<FakeInvoice> FakeInvoices => Set<FakeInvoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(JobsDbContext).Assembly);
}
