namespace Jobs.Worker;

public static class Backoff
{
    /// <summary><c>min(BackoffBase × 2^attempts, BackoffMax) × (1 + random 0..MaxJitter)</c>.</summary>
    public static TimeSpan Compute(int attempts, ExportWorkerOptions options, Random random)
    {
        var exponent = Math.Clamp(attempts, 0, 30);
        var exponential = options.BackoffBase.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(exponential, options.BackoffMax.TotalMilliseconds);
        var jitter = 1 + (random.NextDouble() * options.MaxJitter);

        return TimeSpan.FromMilliseconds(capped * jitter);
    }
}
