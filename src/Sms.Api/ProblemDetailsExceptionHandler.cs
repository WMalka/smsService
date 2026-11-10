using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Sms.Core.Domain;

namespace Sms.Api;

/// <summary>The one place that maps exceptions to HTTP responses (RFC 7807). Never exposes stack traces.</summary>
internal sealed class ProblemDetailsExceptionHandler(IProblemDetailsService problemDetails, ILogger<ProblemDetailsExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = ToProblem(exception);
        if (problem.Status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception");

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }

    private static ProblemDetails ToProblem(Exception exception) => exception switch
    {
        DomainValidationException validation => new HttpValidationProblemDetails(
            validation.Errors.ToDictionary(error => error.Key, error => error.Value))
        {
            Status = StatusCodes.Status400BadRequest,
        },
        MessageNotFoundException notFound => new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound, Title = "Message not found", Detail = notFound.Message,
        },
        // Malformed JSON or a query value of the wrong type.
        BadHttpRequestException badRequest => new ProblemDetails
        {
            Status = badRequest.StatusCode, Title = "Bad request",
        },
        _ => new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError, Title = "An unexpected error occurred",
        },
    };
}
