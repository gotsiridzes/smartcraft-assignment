using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Jobs.Core.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API host. The connection string only
/// matters for commands that touch the database (for example <c>database update</c>).
/// </summary>
public sealed class DesignTimeJobsDbContextFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    private const string LocalConnectionString = "Host=localhost;Port=5432;Database=craftjobs;Username=craftjobs;Password=craftjobs";

    public JobsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Jobs") ?? LocalConnectionString;
        var options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new JobsDbContext(options);
    }
}
