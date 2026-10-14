using Jobs.Core.Domain;

namespace Jobs.Tests.Integration;

internal static class Seed
{
    /// <summary>Total of every seeded job: 2.5 × 650.00 + 10 × 12.35.</summary>
    public const decimal SeededJobTotal = 1748.50m;

    /// <summary>
    /// Creates completed jobs (each with a queued export task) through the domain, straight into the database.
    /// Timestamps are a minute in the past so the tasks are due even if the database clock lags the host.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> CompletedJobsAsync(PostgresFixture postgres, int count)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var ids = new List<Guid>(count);

        await using var db = postgres.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var job = Job.Create($"Customer {i}", $"Site {i}", "Seeded for export tests", now);
            job.AddLine(LineType.Labour, "Electrician hours", 2.5m, 650m, now);
            job.AddLine(LineType.Material, "Cable 3x2.5 mm2", 10m, 12.35m, now);
            job.Complete(now);

            db.Jobs.Add(job);
            ids.Add(job.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }
}
