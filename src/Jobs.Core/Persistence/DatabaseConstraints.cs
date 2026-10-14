namespace Jobs.Core.Persistence;

/// <summary>Constraint names that code reacts to when a unique violation is reported.</summary>
public static class DatabaseConstraints
{
    /// <summary>A violation means another request already stored this Idempotency-Key.</summary>
    public const string IdempotencyKeyPrimaryKey = "pk_idempotency_keys";

    /// <summary>A violation means another request already completed the job (one export per job).</summary>
    public const string ExportTaskUniqueJob = "ix_export_tasks_job_id";
}
