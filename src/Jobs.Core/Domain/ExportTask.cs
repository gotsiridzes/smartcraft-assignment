namespace Jobs.Core.Domain;

/// <summary>
/// One invoice export per completed job. The table is both the outbox and the work queue:
/// it is written in the job's transaction and claimed by workers with SKIP LOCKED.
/// Workers change it only through fenced updates (see the worker's TaskClaimer).
/// </summary>
public sealed class ExportTask
{
    public const int LockedByMaxLength = 200;
    public const int LastErrorMaxLength = 2000;
    public const int ExternalInvoiceIdMaxLength = 100;

    // For EF Core.
    private ExportTask()
    {
    }

    public Guid Id { get; private set; }

    public Guid JobId { get; private set; }

    public ExportTaskStatus Status { get; private set; }

    /// <summary>Number of claims so far. Counted at claim time, so a task that crashes its worker still runs out of attempts.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Compare-and-set token of the current lease holder; only its writes commit.</summary>
    public Guid? LeaseToken { get; private set; }

    public string? LockedBy { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? LastError { get; private set; }

    public string? ExternalInvoiceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    internal static ExportTask Create(Guid jobId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        JobId = jobId,
        Status = ExportTaskStatus.Pending,
        Attempts = 0,
        NextAttemptAt = now,
        CreatedAt = now,
    };
}
