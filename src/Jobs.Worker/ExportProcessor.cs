using Jobs.Core.Persistence;
using Jobs.Worker.Accounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jobs.Worker;

/// <summary>
/// One round of the export loop: claim a batch, call accounting for each task concurrently,
/// and record each outcome with a fenced write. Used by <see cref="ExportWorker"/> and directly by tests.
/// </summary>
public sealed class ExportProcessor(
    TaskClaimer claimer,
    IAccountingClient accounting,
    IDbContextFactory<JobsDbContext> dbContextFactory,
    IOptions<ExportWorkerOptions> options,
    TimeProvider clock,
    ILogger<ExportProcessor> logger)
{
    /// <summary>
    /// Claims and processes one batch. Returns how many tasks were claimed.
    /// <paramref name="cancellationToken"/> only stops the claim: claimed tasks run to the end,
    /// so a shutdown does not abandon a call whose invoice may already exist.
    /// </summary>
    public async Task<int> ProcessBatchAsync(string workerId, CancellationToken cancellationToken)
    {
        var claimed = await claimer.ClaimAsync(workerId, cancellationToken);
        if (claimed.Count == 0)
        {
            return 0;
        }

        // Concurrent, so one lease covers one call instead of a whole sequential batch.
        await Task.WhenAll(claimed.Select(task => ProcessAsync(task, workerId)));
        return claimed.Count;
    }

    private async Task ProcessAsync(ClaimedTask task, string workerId)
    {
        // A message-template scope renders as readable text and still carries each value as a field.
        using var scope = logger.BeginScope(
            "Export task {TaskId} for job {JobId} by worker {WorkerId}, attempt {Attempt}",
            task.Id, task.JobId, workerId, task.Attempts);

        var settings = options.Value;

        try
        {
            if (task.Attempts > settings.MaxAttempts)
            {
                // Earlier claims ended without an outcome (worker crash or lease expiry every time).
                await DeadLetterAsync(task, $"Gave up after {settings.MaxAttempts} attempts that ended without an outcome.");
                return;
            }

            InvoiceRequest invoice;
            await using (var db = await dbContextFactory.CreateDbContextAsync())
            {
                invoice = await InvoiceSnapshot.LoadAsync(db, task.JobId, CancellationToken.None);
            }

            // The task id is the idempotency key: every attempt for this job sends the same key.
            var result = await accounting.CreateInvoiceAsync(invoice, task.Id.ToString(), CancellationToken.None);

            if (await claimer.CompleteAsync(task, result.InvoiceId, clock.GetUtcNow(), CancellationToken.None))
            {
                logger.LogInformation("Exported as invoice {InvoiceId} (replayed: {Replayed}).", result.InvoiceId, result.Replayed);
            }
            else
            {
                logger.LogWarning(
                    "Lease lost before completion; another worker owns the task. Invoice {InvoiceId} is deduplicated by the idempotency key.",
                    result.InvoiceId);
            }
        }
        catch (PermanentAccountingException exception)
        {
            await DeadLetterAsync(task, exception.Message);
        }
        catch (TransientAccountingException exception)
        {
            await RetryOrDeadLetterAsync(task, exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Export attempt failed unexpectedly.");
            await RetryOrDeadLetterAsync(task, exception.Message);
        }
    }

    private async Task RetryOrDeadLetterAsync(ClaimedTask task, string error)
    {
        var settings = options.Value;

        if (task.Attempts >= settings.MaxAttempts)
        {
            await DeadLetterAsync(task, $"Gave up after {task.Attempts} attempts. Last error: {error}");
            return;
        }

        var delay = Backoff.Compute(task.Attempts, settings, Random.Shared);

        try
        {
            if (await claimer.FailAsync(task, delay, error, CancellationToken.None))
            {
                logger.LogWarning("Export failed: {Error}. Retrying in {Delay}.", error, delay);
            }
            else
            {
                logger.LogWarning("Export failed: {Error}. Lease already lost, so another worker owns the retry.", error);
            }
        }
        catch (Exception exception)
        {
            // The lease will expire and the task will be claimed again; the attempt still counts.
            logger.LogError(exception, "Could not record the failed attempt.");
        }
    }

    private async Task DeadLetterAsync(ClaimedTask task, string error)
    {
        try
        {
            if (await claimer.DeadLetterAsync(task, error, clock.GetUtcNow(), CancellationToken.None))
            {
                logger.LogError("Export dead-lettered: {Error}. The job is now ExportFailed and needs manual action.", error);
            }
            else
            {
                logger.LogWarning("Could not dead-letter: lease already lost, another worker owns the task.");
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not dead-letter the task.");
        }
    }
}
