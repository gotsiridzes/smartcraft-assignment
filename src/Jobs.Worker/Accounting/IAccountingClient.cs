namespace Jobs.Worker.Accounting;

/// <summary>The external accounting system. Implementations must honour the idempotency key.</summary>
public interface IAccountingClient
{
    /// <summary>
    /// Creates the invoice, or returns the existing one when the key was seen before.
    /// Throws <see cref="TransientAccountingException"/> for failures worth retrying and
    /// <see cref="PermanentAccountingException"/> for rejections that will not succeed on retry.
    /// </summary>
    Task<InvoiceResult> CreateInvoiceAsync(InvoiceRequest invoice, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed record InvoiceRequest(Guid JobId, string CustomerName, string SiteAddress, IReadOnlyList<InvoiceLine> Lines, decimal Total);

public sealed record InvoiceLine(string Type, string Description, decimal Quantity, decimal UnitPrice, decimal Total);

/// <param name="InvoiceId">The accounting system's id for the invoice.</param>
/// <param name="Replayed">True when the key was seen before and the existing invoice was returned.</param>
public sealed record InvoiceResult(string InvoiceId, bool Replayed);

public sealed class TransientAccountingException(string message) : Exception(message);

public sealed class PermanentAccountingException(string message) : Exception(message);
