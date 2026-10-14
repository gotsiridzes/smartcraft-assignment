using Microsoft.Extensions.Options;
using Npgsql;

namespace Jobs.Worker.Accounting;

/// <summary>
/// Simulated external accounting system: slow, sometimes down, and idempotent by key.
/// It writes on its own connection and transaction, independent of the worker's unit of work,
/// so a worker crash after this call behaves like a crash after a real HTTP call.
/// </summary>
public sealed class FakeAccountingClient(NpgsqlDataSource dataSource, IOptions<FakeAccountingOptions> options) : IAccountingClient
{
    // ON CONFLICT makes the dedup race-safe: a stale and a current lease holder may call at the same time.
    private const string InsertSql = """
        INSERT INTO fake_accounting.invoices (id, idempotency_key, job_id, total, created_at)
        VALUES ($1, $2, $3, $4, now())
        ON CONFLICT (idempotency_key) DO NOTHING
        RETURNING id
        """;

    private const string SelectSql = "SELECT id FROM fake_accounting.invoices WHERE idempotency_key = $1";

    private int _replayCount;

    /// <summary>How many calls were answered from an existing invoice. Lets tests prove the dedup path ran.</summary>
    public int ReplayCount => Volatile.Read(ref _replayCount);

    public async Task<InvoiceResult> CreateInvoiceAsync(InvoiceRequest invoice, string idempotencyKey, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        await Task.Delay(RandomLatency(settings), cancellationToken);

        if (Chance(settings.PermanentFailureRate))
        {
            throw new PermanentAccountingException("Simulated rejection: the accounting system refused the invoice.");
        }

        if (Chance(settings.FailBeforeRate))
        {
            throw new TransientAccountingException("Simulated outage: the accounting system is unavailable.");
        }

        var (invoiceId, replayed) = await InsertOrGetAsync(invoice, idempotencyKey, cancellationToken);
        if (replayed)
        {
            Interlocked.Increment(ref _replayCount);
        }

        if (Chance(settings.FailAfterRate))
        {
            throw new TransientAccountingException("Simulated lost response: the invoice was created but the reply never arrived.");
        }

        return new InvoiceResult(invoiceId.ToString(), replayed);
    }

    private async Task<(Guid InvoiceId, bool Replayed)> InsertOrGetAsync(InvoiceRequest invoice, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var insert = new NpgsqlCommand(InsertSql, connection))
        {
            insert.Parameters.AddWithValue(Guid.CreateVersion7());
            insert.Parameters.AddWithValue(idempotencyKey);
            insert.Parameters.AddWithValue(invoice.JobId);
            insert.Parameters.AddWithValue(invoice.Total);

            if (await insert.ExecuteScalarAsync(cancellationToken) is Guid createdId)
            {
                return (createdId, false);
            }
        }

        await using var select = new NpgsqlCommand(SelectSql, connection);
        select.Parameters.AddWithValue(idempotencyKey);

        var existingId = await select.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Invoice for idempotency key '{idempotencyKey}' vanished after a conflict.");
        return ((Guid)existingId, true);
    }

    private static TimeSpan RandomLatency(FakeAccountingOptions settings)
        => settings.LatencyMin + ((settings.LatencyMax - settings.LatencyMin) * Random.Shared.NextDouble());

    private static bool Chance(double rate) => rate > 0 && Random.Shared.NextDouble() < rate;
}
