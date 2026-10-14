namespace Jobs.Core.Persistence.FakeAccounting;

/// <summary>
/// Simulation only: the invoice table of the fake external accounting system. It lives in its own
/// schema of the same database to keep the assignment self-contained. The unique idempotency key
/// is the receiver-side dedup a real accounting API is expected to provide.
/// </summary>
public sealed class FakeInvoice
{
    public const int IdempotencyKeyMaxLength = 100;

    // For EF Core. The fake client writes rows with plain SQL; EF reads them in tests.
    private FakeInvoice()
    {
        IdempotencyKey = null!;
    }

    public Guid Id { get; private set; }

    public string IdempotencyKey { get; private set; }

    public Guid JobId { get; private set; }

    public decimal Total { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
