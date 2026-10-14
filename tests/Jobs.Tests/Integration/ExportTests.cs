using Jobs.Core.Domain;
using Jobs.Worker;
using Jobs.Worker.Accounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jobs.Tests.Integration;

/// <summary>Tests 1 and 2: every completed job produces exactly one invoice, even with competing workers.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ExportTests(PostgresFixture postgres) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await postgres.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_completed_job_is_invoiced_exactly_once_under_contention()
    {
        const int jobCount = 200;
        const int workerCount = 4;
        var jobIds = await Seed.CompletedJobsAsync(postgres, jobCount);

        // Production uses a 30 s lease and second-scale backoff; here everything is fast, and
        // MaxAttempts is high so random failures never dead-letter a task and make the test flaky.
        var workerOptions = Options.Create(new ExportWorkerOptions
        {
            BatchSize = 5,
            LeaseDuration = TimeSpan.FromMilliseconds(500),
            MaxAttempts = 50,
            BackoffBase = TimeSpan.FromMilliseconds(10),
            BackoffMax = TimeSpan.FromMilliseconds(200),
            MaxJitter = 0.2,
            PollInterval = TimeSpan.FromMilliseconds(50),
        });

        // Latency sometimes exceeds the lease, so workers really do take tasks over from each other.
        // FailAfter writes the invoice and then fails: only the idempotency key prevents a second invoice.
        var accounting = new FakeAccountingClient(postgres.DataSource, Options.Create(new FakeAccountingOptions
        {
            LatencyMin = TimeSpan.Zero,
            LatencyMax = TimeSpan.FromMilliseconds(600),
            FailBeforeRate = 0.1,
            FailAfterRate = 0.1,
        }));
        var processor = CreateProcessor(accounting, workerOptions);

        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(1, workerCount)
            .Select(i => Task.Run(() => RunWorkerAsync(processor, $"worker-{i}", workerOptions.Value.PollInterval, stop.Token)))
            .ToArray();

        try
        {
            await WaitUntilDrainedAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(workers);
        }

        await using var db = postgres.CreateDbContext();

        var tasks = await db.ExportTasks.AsNoTracking().ToListAsync();
        tasks.Count.ShouldBe(jobCount);
        tasks.ShouldAllBe(t => t.Status == ExportTaskStatus.Succeeded);

        var jobs = await db.Jobs.AsNoTracking().Include(j => j.Lines).ToListAsync();
        jobs.ShouldAllBe(j => j.Status == JobStatus.Exported);

        // Exactly one invoice per job. Counting per job, not per key: the unique key constraint
        // alone would make a "no duplicate keys" check pass even if a job had two export tasks.
        var invoices = await db.FakeInvoices.AsNoTracking().ToListAsync();
        invoices.Count.ShouldBe(jobCount);
        invoices.GroupBy(i => i.JobId).ShouldAllBe(group => group.Count() == 1);
        invoices.Select(i => i.JobId).ToHashSet().SetEquals(jobIds).ShouldBeTrue();

        var invoiceByJob = invoices.ToDictionary(i => i.JobId);
        foreach (var task in tasks)
        {
            task.ExternalInvoiceId.ShouldBe(invoiceByJob[task.JobId].Id.ToString());
        }

        foreach (var job in jobs)
        {
            invoiceByJob[job.Id].Total.ShouldBe(job.Total);
            job.Total.ShouldBe(Seed.SeededJobTotal);
        }

        // Proves the run actually exercised the crash window, not just the happy path.
        accounting.ReplayCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_stale_lease_holder_cannot_record_an_outcome()
    {
        var jobId = (await Seed.CompletedJobsAsync(postgres, 1)).Single();
        var options = Options.Create(new ExportWorkerOptions { BatchSize = 1, LeaseDuration = TimeSpan.FromSeconds(30) });
        var claimer = new TaskClaimer(postgres.DataSource, postgres, options);
        var accounting = InstantAccounting();

        var claimedByA = await claimer.ClaimAsync("worker-a", CancellationToken.None);
        claimedByA.Count.ShouldBe(1);
        var leaseA = claimedByA[0];

        // While A's lease is valid nobody else can take the task.
        (await claimer.ClaimAsync("worker-b", CancellationToken.None)).ShouldBeEmpty();

        // Worker A pauses past its lease (GC pause, network stall); worker B takes over.
        await ExpireLeaseAsync(leaseA.Id);
        var claimedByB = await claimer.ClaimAsync("worker-b", CancellationToken.None);
        claimedByB.Count.ShouldBe(1);
        var leaseB = claimedByB[0];
        leaseB.Id.ShouldBe(leaseA.Id);
        leaseB.LeaseToken.ShouldNotBe(leaseA.LeaseToken);
        leaseB.Attempts.ShouldBe(2);

        var invoice = await LoadInvoiceAsync(jobId);
        var now = DateTimeOffset.UtcNow;

        var resultB = await accounting.CreateInvoiceAsync(invoice, leaseB.Id.ToString(), CancellationToken.None);
        (await claimer.CompleteAsync(leaseB, resultB.InvoiceId, now, CancellationToken.None)).ShouldBeTrue();

        // Worker A wakes up and carries on with its stale lease.
        var resultA = await accounting.CreateInvoiceAsync(invoice, leaseA.Id.ToString(), CancellationToken.None);
        resultA.Replayed.ShouldBeTrue();
        resultA.InvoiceId.ShouldBe(resultB.InvoiceId);

        (await claimer.CompleteAsync(leaseA, resultA.InvoiceId, now, CancellationToken.None)).ShouldBeFalse();
        (await claimer.FailAsync(leaseA, TimeSpan.FromSeconds(5), "late failure", CancellationToken.None)).ShouldBeFalse();
        (await claimer.DeadLetterAsync(leaseA, "late dead-letter", now, CancellationToken.None)).ShouldBeFalse();

        await using var db = postgres.CreateDbContext();
        (await db.FakeInvoices.CountAsync()).ShouldBe(1);

        var task = await db.ExportTasks.AsNoTracking().SingleAsync();
        task.Status.ShouldBe(ExportTaskStatus.Succeeded);
        task.LockedBy.ShouldBe("worker-b");
        task.LeaseToken.ShouldBe(leaseB.LeaseToken);
        task.Attempts.ShouldBe(2);
        task.ExternalInvoiceId.ShouldBe(resultB.InvoiceId);

        (await db.Jobs.AsNoTracking().SingleAsync()).Status.ShouldBe(JobStatus.Exported);
    }

    [Fact]
    public async Task A_task_that_keeps_losing_its_worker_is_dead_lettered()
    {
        await Seed.CompletedJobsAsync(postgres, 1);
        var options = Options.Create(new ExportWorkerOptions { BatchSize = 1, MaxAttempts = 2 });
        var claimer = new TaskClaimer(postgres.DataSource, postgres, options);

        // Two claims whose workers "crash": the leases simply expire without an outcome.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var claimed = await claimer.ClaimAsync($"crashing-worker-{attempt}", CancellationToken.None);
            claimed.Count.ShouldBe(1);
            await ExpireLeaseAsync(claimed[0].Id);
        }

        var accounting = InstantAccounting();
        var processed = await CreateProcessor(accounting, options).ProcessBatchAsync("worker-c", CancellationToken.None);
        processed.ShouldBe(1);

        await using var db = postgres.CreateDbContext();
        var task = await db.ExportTasks.AsNoTracking().SingleAsync();
        task.Status.ShouldBe(ExportTaskStatus.DeadLettered);
        task.Attempts.ShouldBe(3);
        task.LastError.ShouldNotBeNullOrWhiteSpace();
        (await db.Jobs.AsNoTracking().SingleAsync()).Status.ShouldBe(JobStatus.ExportFailed);
        (await db.FakeInvoices.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_permanent_rejection_dead_letters_without_retrying()
    {
        await Seed.CompletedJobsAsync(postgres, 1);
        var options = Options.Create(new ExportWorkerOptions { BatchSize = 1 });
        var accounting = new FakeAccountingClient(postgres.DataSource, Options.Create(new FakeAccountingOptions
        {
            LatencyMin = TimeSpan.Zero,
            LatencyMax = TimeSpan.Zero,
            PermanentFailureRate = 1,
        }));

        (await CreateProcessor(accounting, options).ProcessBatchAsync("worker-a", CancellationToken.None)).ShouldBe(1);

        await using var db = postgres.CreateDbContext();
        var task = await db.ExportTasks.AsNoTracking().SingleAsync();
        task.Status.ShouldBe(ExportTaskStatus.DeadLettered);
        task.Attempts.ShouldBe(1);
        (await db.Jobs.AsNoTracking().SingleAsync()).Status.ShouldBe(JobStatus.ExportFailed);
    }

    private ExportProcessor CreateProcessor(IAccountingClient accounting, IOptions<ExportWorkerOptions> options)
        => new(
            new TaskClaimer(postgres.DataSource, postgres, options),
            accounting,
            postgres,
            options,
            TimeProvider.System,
            NullLogger<ExportProcessor>.Instance);

    private FakeAccountingClient InstantAccounting()
        => new(postgres.DataSource, Options.Create(new FakeAccountingOptions { LatencyMin = TimeSpan.Zero, LatencyMax = TimeSpan.Zero }));

    private static async Task RunWorkerAsync(ExportProcessor processor, string workerId, TimeSpan pollInterval, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            int claimed;
            try
            {
                claimed = await processor.ProcessBatchAsync(workerId, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }

            if (claimed > 0)
            {
                continue;
            }

            try
            {
                await Task.Delay(pollInterval, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task WaitUntilDrainedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await using var db = postgres.CreateDbContext();
            var open = await db.ExportTasks.CountAsync(t =>
                t.Status == ExportTaskStatus.Pending || t.Status == ExportTaskStatus.Processing);
            if (open == 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{open} export tasks were still open after {timeout}.");
            }

            await Task.Delay(100);
        }
    }

    private async Task ExpireLeaseAsync(Guid taskId)
    {
        await using var db = postgres.CreateDbContext();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE export_tasks SET lease_until = now() - interval '1 second' WHERE id = {taskId}");
    }

    private async Task<InvoiceRequest> LoadInvoiceAsync(Guid jobId)
    {
        await using var db = postgres.CreateDbContext();
        return await InvoiceSnapshot.LoadAsync(db, jobId, CancellationToken.None);
    }
}
