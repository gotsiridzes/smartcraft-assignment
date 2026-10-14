namespace Jobs.Worker;

public sealed class ExportWorkerOptions
{
    public const string SectionName = "ExportWorker";

    /// <summary>Tasks claimed per round trip. They are processed concurrently, so one lease covers one call.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>How long a claim is exclusive. After it expires another worker may take the task over.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Claims allowed before the task is dead-lettered. Crashed attempts count too.</summary>
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound of the random extra delay, as a fraction of the backoff (0.2 = up to 20 %).</summary>
    public double MaxJitter { get; set; } = 0.2;

    /// <summary>Delay before polling again when nothing was claimed.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public bool IsValid()
        => BatchSize > 0
        && LeaseDuration > TimeSpan.Zero
        && MaxAttempts > 0
        && BackoffBase > TimeSpan.Zero
        && BackoffMax >= BackoffBase
        && MaxJitter >= 0
        && PollInterval > TimeSpan.Zero;
}
