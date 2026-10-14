using System.Text.Json;
using Jobs.Api.Contracts;
using Jobs.Api.Http;
using Jobs.Core.Domain;
using Jobs.Core.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Jobs.Api.Endpoints;

public static class JobEndpoints
{
    private const int ListLimit = 50;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/jobs").WithTags("Jobs");

        jobs.MapPost("", CreateJob)
            .WithName("CreateJob")
            .WithSummary("Create a job. Requires an Idempotency-Key header; a retry with the same key replays the first response.")
            .Produces<JobResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        jobs.MapGet("", ListJobs)
            .WithName("ListJobs")
            .WithSummary($"List jobs, newest first, optionally filtered by status. Returns at most {ListLimit}.")
            .Produces<List<JobResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        jobs.MapGet("/{id:guid}", GetJob)
            .WithName("GetJob")
            .WithSummary("Get a job with its lines, total and export status. The ETag is the job version.")
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapPatch("/{id:guid}", UpdateJob)
            .WithName("UpdateJob")
            .WithSummary("Change the job header. Null fields stay unchanged. Requires If-Match.")
            .ProducesJobWrite();

        jobs.MapPost("/{id:guid}/lines", AddLine)
            .WithName("AddLine")
            .WithSummary("Add a line. The first line moves a Draft job to InProgress. Requires the job's If-Match.")
            .ProducesJobWrite();

        jobs.MapPatch("/{id:guid}/lines/{lineId:guid}", UpdateLine)
            .WithName("UpdateLine")
            .WithSummary("Change a line. Null fields stay unchanged. Requires the job's If-Match.")
            .ProducesJobWrite();

        jobs.MapDelete("/{id:guid}/lines/{lineId:guid}", RemoveLine)
            .WithName("RemoveLine")
            .WithSummary("Delete a line. Requires the job's If-Match.")
            .ProducesJobWrite();

        jobs.MapPost("/{id:guid}/complete", CompleteJob)
            .WithName("CompleteJob")
            .WithSummary("Freeze the job and queue its invoice export in the same transaction. Requires If-Match.")
            .ProducesJobWrite();

        return app;
    }

    private static async Task<IResult> CreateJob(
        CreateJobRequest request,
        [FromHeader(Name = Idempotency.HeaderName)] string? idempotencyKey,
        HttpContext http,
        JobsDbContext db,
        TimeProvider clock,
        IOptions<HttpJsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        if (!Idempotency.TryValidateKey(idempotencyKey, out var keyProblem))
        {
            return keyProblem;
        }

        var serializerOptions = jsonOptions.Value.SerializerOptions;
        var requestHash = Idempotency.HashRequest(request, serializerOptions);

        // Fast path for sequential retries. Concurrent duplicates are caught by the primary key below.
        var existing = await db.IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Key == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return Idempotency.Replay(http, existing, requestHash);
        }

        var now = clock.GetUtcNow();
        var job = Job.Create(request.CustomerName, request.SiteAddress, request.Description, now);
        var etag = ETags.Format(job.Version);
        var body = JsonSerializer.Serialize(JobResponse.From(job), serializerOptions);

        db.Jobs.Add(job);
        db.IdempotencyKeys.Add(new IdempotencyRecord
        {
            Key = idempotencyKey,
            RequestHash = requestHash,
            ResponseStatus = StatusCodes.Status201Created,
            ResponseBody = body,
            ResponseEtag = etag,
            JobId = job.Id,
            CreatedAt = now,
        });

        try
        {
            // Job and key commit together. A concurrent request with the same key blocks on the
            // primary key until this transaction ends, then fails here and replays our response.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (Idempotency.IsKeyConflict(exception))
        {
            db.ChangeTracker.Clear();
            var stored = await db.IdempotencyKeys.AsNoTracking()
                .SingleAsync(r => r.Key == idempotencyKey, cancellationToken);
            return Idempotency.Replay(http, stored, requestHash);
        }

        return Idempotency.StoredResponse(http, StatusCodes.Status201Created, body, etag, job.Id);
    }

    private static async Task<IResult> GetJob(Guid id, HttpContext http, JobsDbContext db, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .Include(j => j.Lines)
            .Include(j => j.Export)
            .SingleOrDefaultAsync(j => j.Id == id, cancellationToken);

        return job is null ? Problems.JobNotFound(id) : JobOk(http, job);
    }

    private static async Task<IResult> ListJobs(string? status, JobsDbContext db, CancellationToken cancellationToken)
    {
        var query = db.Jobs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var name = Enum.GetNames<JobStatus>()
                .FirstOrDefault(n => string.Equals(n, status, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return Problems.InvalidStatus(status);
            }

            var filter = Enum.Parse<JobStatus>(name);
            query = query.Where(j => j.Status == filter);
        }

        var jobs = await query
            .Include(j => j.Lines)
            .Include(j => j.Export)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Take(ListLimit)
            .ToListAsync(cancellationToken);

        return Results.Ok(jobs.Select(JobResponse.From).ToList());
    }

    private static Task<IResult> UpdateJob(
        Guid id,
        UpdateJobRequest request,
        [FromHeader(Name = ETags.IfMatchHeader)] string? ifMatch,
        HttpContext http,
        JobsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => MutateAsync(http, db, id, ifMatch,
            job => job.Update(request.CustomerName, request.SiteAddress, request.Description, clock.GetUtcNow()),
            cancellationToken);

    private static Task<IResult> AddLine(
        Guid id,
        AddLineRequest request,
        [FromHeader(Name = ETags.IfMatchHeader)] string? ifMatch,
        HttpContext http,
        JobsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => MutateAsync(http, db, id, ifMatch,
            job => job.AddLine(request.Type, request.Description, request.Quantity, request.UnitPrice, clock.GetUtcNow()),
            cancellationToken);

    private static Task<IResult> UpdateLine(
        Guid id,
        Guid lineId,
        UpdateLineRequest request,
        [FromHeader(Name = ETags.IfMatchHeader)] string? ifMatch,
        HttpContext http,
        JobsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => MutateAsync(http, db, id, ifMatch,
            job => job.UpdateLine(lineId, request.Type, request.Description, request.Quantity, request.UnitPrice, clock.GetUtcNow()),
            cancellationToken);

    private static Task<IResult> RemoveLine(
        Guid id,
        Guid lineId,
        [FromHeader(Name = ETags.IfMatchHeader)] string? ifMatch,
        HttpContext http,
        JobsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => MutateAsync(http, db, id, ifMatch,
            job => job.RemoveLine(lineId, clock.GetUtcNow()),
            cancellationToken);

    private static Task<IResult> CompleteJob(
        Guid id,
        [FromHeader(Name = ETags.IfMatchHeader)] string? ifMatch,
        HttpContext http,
        JobsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => MutateAsync(http, db, id, ifMatch,
            job => job.Complete(clock.GetUtcNow()),
            cancellationToken);

    /// <summary>
    /// Shared write path: 428/400 for the header, 404, 412 for a stale version, then the domain
    /// change (409/404/400 via <see cref="DomainExceptionHandler"/>), then a save whose UPDATE carries
    /// <c>WHERE version = @original</c>, so a race between read and write is also a 412.
    /// </summary>
    private static async Task<IResult> MutateAsync(
        HttpContext http,
        JobsDbContext db,
        Guid id,
        string? ifMatch,
        Action<Job> change,
        CancellationToken cancellationToken)
    {
        if (!ETags.TryParseIfMatch(ifMatch, out var expectedVersion, out var headerProblem))
        {
            return headerProblem;
        }

        var job = await db.Jobs
            .Include(j => j.Lines)
            .Include(j => j.Export)
            .SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
        {
            return Problems.JobNotFound(id);
        }

        // Checked before the domain call, so a stale client re-fetches instead of getting
        // a misleading answer (for example a 404 for a line someone else deleted).
        if (job.Version != expectedVersion)
        {
            return Problems.VersionMismatch();
        }

        change(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problems.VersionMismatch();
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: DatabaseConstraints.ExportTaskUniqueJob,
        })
        {
            // A concurrent complete won the race; the database's one-export-per-job rule caught it.
            return Problems.VersionMismatch();
        }

        return JobOk(http, job);
    }

    private static IResult JobOk(HttpContext http, Job job)
    {
        http.Response.Headers.ETag = ETags.Format(job.Version);
        return Results.Ok(JobResponse.From(job));
    }

    private static RouteHandlerBuilder ProducesJobWrite(this RouteHandlerBuilder builder) => builder
        .Produces<JobResponse>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesProblem(StatusCodes.Status428PreconditionRequired);
}
