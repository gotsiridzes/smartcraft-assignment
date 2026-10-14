using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jobs.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jobs.Api.Http;

internal static class Idempotency
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    public static bool TryValidateKey([NotNullWhen(true)] string? key, [NotNullWhen(false)] out IResult? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(key))
        {
            problem = Problems.IdempotencyKeyMissing($"Send a unique {HeaderName} header (for example a UUID) with every POST /jobs.");
            return false;
        }

        if (key.Length > IdempotencyRecord.KeyMaxLength)
        {
            problem = Problems.IdempotencyKeyMissing($"{HeaderName} must be at most {IdempotencyRecord.KeyMaxLength} characters.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Hashes the bound request rather than the raw bytes, so whitespace or property order
    /// differences in a retried request do not count as "a different request".
    /// </summary>
    public static string HashRequest<TRequest>(TRequest request, JsonSerializerOptions options)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, options)));

    /// <summary>True when the insert failed because another request already stored this key.</summary>
    public static bool IsKeyConflict(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: DatabaseConstraints.IdempotencyKeyPrimaryKey,
        };

    /// <summary>Returns the stored first response, or 422 when the key was used for a different body.</summary>
    public static IResult Replay(HttpContext http, IdempotencyRecord stored, string requestHash)
    {
        if (!string.Equals(stored.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Problems.IdempotencyKeyReused();
        }

        http.Response.Headers[ReplayedHeaderName] = "true";
        return StoredResponse(http, stored.ResponseStatus, stored.ResponseBody, stored.ResponseEtag, stored.JobId);
    }

    /// <summary>Writes a response from its stored parts. Used for both the first response and replays, so they match byte for byte.</summary>
    public static IResult StoredResponse(HttpContext http, int status, string body, string etag, Guid jobId)
    {
        http.Response.Headers.ETag = etag;
        http.Response.Headers.Location = $"/jobs/{jobId}";
        return Results.Text(body, "application/json", Encoding.UTF8, status);
    }
}
