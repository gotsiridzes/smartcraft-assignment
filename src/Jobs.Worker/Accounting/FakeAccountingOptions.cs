namespace Jobs.Worker.Accounting;

public sealed class FakeAccountingOptions
{
    public const string SectionName = "FakeAccounting";

    public TimeSpan LatencyMin { get; set; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan LatencyMax { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Share of calls that fail before the invoice is written (nothing to deduplicate).</summary>
    public double FailBeforeRate { get; set; }

    /// <summary>
    /// Share of calls that write the invoice and then fail, as if the response was lost.
    /// The retry must be answered from the idempotency key, not create a second invoice.
    /// </summary>
    public double FailAfterRate { get; set; }

    /// <summary>Share of calls rejected for good (for example invalid customer data).</summary>
    public double PermanentFailureRate { get; set; }

    public bool IsValid()
        => LatencyMin >= TimeSpan.Zero
        && LatencyMax >= LatencyMin
        && IsRate(FailBeforeRate)
        && IsRate(FailAfterRate)
        && IsRate(PermanentFailureRate);

    private static bool IsRate(double value) => value is >= 0 and <= 1;
}
