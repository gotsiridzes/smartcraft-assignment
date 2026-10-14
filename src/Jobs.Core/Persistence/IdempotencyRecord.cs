namespace Jobs.Core.Persistence;

/// <summary>
/// Stored first response for an Idempotency-Key. Inserted in the same transaction as the job,
/// so the primary key serialises concurrent duplicates without an "in progress" state.
/// </summary>
public sealed class IdempotencyRecord
{
    public const int KeyMaxLength = 100;

    public required string Key { get; init; }

    /// <summary>Hex SHA-256 of the canonical request body.</summary>
    public required string RequestHash { get; init; }

    public required int ResponseStatus { get; init; }

    /// <summary>Response body exactly as first sent, so a replay is byte-identical.</summary>
    public required string ResponseBody { get; init; }

    public required string ResponseEtag { get; init; }

    public required Guid JobId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
