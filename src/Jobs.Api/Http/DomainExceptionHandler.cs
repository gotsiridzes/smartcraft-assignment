using Jobs.Core.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Jobs.Api.Http;

/// <summary>Maps domain exceptions to ProblemDetails with stable types. Anything else stays a 500.</summary>
internal sealed class DomainExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
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
