using Jobs.Core.Domain;
using Jobs.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Jobs.Worker;

/// <summary>A task this worker holds. <see cref="LeaseToken"/> fences every later write.</summary>
public sealed record ClaimedTask(Guid Id, Guid JobId, int Attempts, Guid LeaseToken);

/// <summary>
/// Claims export tasks and records their outcome. Every outcome write is fenced by the lease
/// token: if the lease expired and another worker re-claimed the task, the write updates zero
/// rows and the method returns <c>false</c>. Plain SQL is used where scheduling relies on the
/// database clock (claim, retry); EF Core's ExecuteUpdate for the transactional outcomes.
/// </summary>
public sealed class TaskClaimer(
    NpgsqlDataSource dataSource,
    IDbContextFactory<JobsDbContext> dbContextFactory,
    IOptions<ExportWorkerOptions> options)
{
    // One statement: pick due tasks (or tasks whose lease expired) without waiting on rows other
    // workers hold, then take the lease and count the attempt. Times come from the database clock.
    private const string ClaimSql = """
        WITH next AS (
            SELECT id
            FROM export_tasks
            WHERE (status = 'Pending' AND next_attempt_at <= now())
               OR (status = 'Processing' AND lease_until < now())
            ORDER BY next_attempt_at
            LIMIT $1
            FOR UPDATE SKIP LOCKED
        )
        UPDATE export_tasks AS t
        SET status = 'Processing',
            lease_token = $2,
            locked_by = $3,
            lease_until = now() + $4,
            attempts = t.attempts + 1
        FROM next
        WHERE t.id = next.id
        RETURNING t.id, t.job_id, t.attempts, t.lease_token
        """;

    // Fenced like every other outcome write: id + lease token + still Processing.
    private const string FailSql = """
        UPDATE export_tasks
        SET status = 'Pending',
            next_attempt_at = now() + $1,
            lease_token = NULL,
            locked_by = NULL,
            lease_until = NULL,
            last_error = $2
        WHERE id = $3
          AND lease_token = $4
          AND status = 'Processing'
        """;

    public async Task<IReadOnlyList<ClaimedTask>> ClaimAsync(string workerId, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        await using var command = dataSource.CreateCommand(ClaimSql);
        command.Parameters.AddWithValue(settings.BatchSize);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(settings.LeaseDuration);

        var claimed = new List<ClaimedTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedTask(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetGuid(3)));
        }

        return claimed;
    }

    /// <summary>Marks the task Succeeded and the job Exported in one transaction.</summary>
    public async Task<bool> CompleteAsync(ClaimedTask task, string externalInvoiceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var updated = await Fenced(db, task).ExecuteUpdateAsync(setters => setters
            .SetProperty(t => t.Status, ExportTaskStatus.Succeeded)
            .SetProperty(t => t.ExternalInvoiceId, externalInvoiceId)
            .SetProperty(t => t.LastError, (string?)null)
            .SetProperty(t => t.CompletedAt, (DateTimeOffset?)now),
            cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        var job = await db.Jobs.SingleAsync(j => j.Id == task.JobId, cancellationToken);
        job.MarkExported(now);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Releases the task for another attempt after <paramref name="retryAfter"/>. The due time is
    /// computed with the database clock, the same clock the claim compares against.
    /// </summary>
    public async Task<bool> FailAsync(ClaimedTask task, TimeSpan retryAfter, string error, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(FailSql);
        command.Parameters.AddWithValue(retryAfter);
        command.Parameters.AddWithValue(Truncate(error));
        command.Parameters.AddWithValue(task.Id);
        command.Parameters.AddWithValue(task.LeaseToken);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Gives up on the task and marks the job ExportFailed in one transaction.</summary>
    public async Task<bool> DeadLetterAsync(ClaimedTask task, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var updated = await Fenced(db, task).ExecuteUpdateAsync(setters => setters
            .SetProperty(t => t.Status, ExportTaskStatus.DeadLettered)
            .SetProperty(t => t.LastError, Truncate(error))
            .SetProperty(t => t.CompletedAt, (DateTimeOffset?)now),
            cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        var job = await db.Jobs.SingleAsync(j => j.Id == task.JobId, cancellationToken);
        job.MarkExportFailed(now);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The fencing condition: only the current lease holder of a task still in flight may write.</summary>
    private static IQueryable<ExportTask> Fenced(JobsDbContext db, ClaimedTask task)
        => db.ExportTasks.Where(t =>
            t.Id == task.Id
            && t.LeaseToken == task.LeaseToken
            && t.Status == ExportTaskStatus.Processing);

    private static string Truncate(string error)
        => error.Length <= ExportTask.LastErrorMaxLength ? error : error[..ExportTask.LastErrorMaxLength];
}
