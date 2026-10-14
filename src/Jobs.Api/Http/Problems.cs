using Jobs.Core.Domain;

namespace Jobs.Api.Http;

/// <summary>Stable ProblemDetails <c>type</c> values, so clients can tell a 409 state conflict from a 412 version conflict.</summary>
public static class ProblemTypes
{
    private const string Base = "https://craft-jobs.example/problems/";

    public const string Validation = Base + "validation-error";
    public const string InvalidRequest = Base + "invalid-request";
    public const string IdempotencyKeyMissing = Base + "idempotency-key-missing";
    public const string InvalidIfMatch = Base + "invalid-if-match";
    public const string InvalidStatus = Base + "invalid-status";
    public const string JobNotFound = Base + "job-not-found";
    public const string LineNotFound = Base + "line-not-found";
    public const string JobStateConflict = Base + "job-state-conflict";
    public const string VersionMismatch = Base + "version-mismatch";
    public const string IdempotencyKeyReused = Base + "idempotency-key-reused";
    public const string PreconditionRequired = Base + "precondition-required";
}

internal static class Problems
{
    public static IResult IdempotencyKeyMissing(string detail) => Results.Problem(
        type: ProblemTypes.IdempotencyKeyMissing,
        title: "A valid Idempotency-Key header is required.",
        detail: detail,
        statusCode: StatusCodes.Status400BadRequest);

    public static IResult InvalidIfMatch() => Results.Problem(
        type: ProblemTypes.InvalidIfMatch,
        title: "The If-Match header is malformed.",
        detail: "Send the strong ETag from the last response unchanged, for example If-Match: \"7\".",
        statusCode: StatusCodes.Status400BadRequest);

    public static IResult InvalidStatus(string status) => Results.Problem(
        type: ProblemTypes.InvalidStatus,
        title: "Unknown job status.",
        detail: $"'{status}' is not a job status. Use one of: {string.Join(", ", Enum.GetNames<JobStatus>())}.",
        statusCode: StatusCodes.Status400BadRequest);

    public static IResult JobNotFound(Guid id) => Results.Problem(
        type: ProblemTypes.JobNotFound,
        title: "Job not found.",
        detail: $"Job '{id}' does not exist.",
        statusCode: StatusCodes.Status404NotFound);

    public static IResult VersionMismatch() => Results.Problem(
        type: ProblemTypes.VersionMismatch,
        title: "The job was changed by someone else.",
        detail: "Your If-Match version is stale. Fetch the job again and retry with the new ETag.",
        statusCode: StatusCodes.Status412PreconditionFailed);

    public static IResult IdempotencyKeyReused() => Results.Problem(
        type: ProblemTypes.IdempotencyKeyReused,
        title: "Idempotency-Key reused with a different request.",
        detail: "This key was already used for a request with a different body. Use a new key for a new request.",
        statusCode: StatusCodes.Status422UnprocessableEntity);

    public static IResult PreconditionRequired() => Results.Problem(
        type: ProblemTypes.PreconditionRequired,
        title: "If-Match header required.",
        detail: "Writes to a job must carry the job's current ETag in If-Match, for example If-Match: \"7\".",
        statusCode: StatusCodes.Status428PreconditionRequired);
}
