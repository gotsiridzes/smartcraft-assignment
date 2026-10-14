using System.Text.Json;
using Jobs.Core.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Jobs.Api.Http;

/// <summary>
/// Maps domain exceptions, and requests the framework could not read, to ProblemDetails with
/// stable types. These are client errors, so they are answered without an error log. Anything
/// else stays an unhandled 500.
/// </summary>
internal sealed class DomainExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            // Thrown by minimal APIs in Development for a body or parameter that cannot be bound
            // (in other environments the framework answers 400 itself without throwing).
            BadHttpRequestException badRequest => new ProblemDetails
            {
                Type = ProblemTypes.InvalidRequest,
                Title = "The request could not be read.",
                Detail = badRequest.InnerException is JsonException json
                    ? $"The body is not valid JSON for this endpoint: {json.Message}"
                    : badRequest.Message,
                Status = badRequest.StatusCode,
            },
            DomainValidationException validation => new HttpValidationProblemDetails(new Dictionary<string, string[]>(validation.Errors))
            {
                Type = ProblemTypes.Validation,
                Title = "One or more fields are invalid.",
                Status = StatusCodes.Status400BadRequest,
            },
            LineNotFoundException notFound => new ProblemDetails
            {
                Type = ProblemTypes.LineNotFound,
                Title = "Line not found.",
                Detail = notFound.Message,
                Status = StatusCodes.Status404NotFound,
            },
            JobStateConflictException conflict => new ProblemDetails
            {
                Type = ProblemTypes.JobStateConflict,
                Title = "The job's state does not allow this change.",
                Detail = conflict.Message,
                Status = StatusCodes.Status409Conflict,
            },
            _ => null,
        };

        if (problem is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
