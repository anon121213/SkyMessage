using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Infrastructure;

/// <summary>Maps exceptions to RFC 7807 problem details carrying a machine-readable "code".</summary>
internal sealed class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, errors) = exception switch
        {
            AppException app => (app.StatusCode, app.Code, app.Message, app.Errors),
            FluentValidation.ValidationException validation => (
                StatusCodes.Status400BadRequest,
                ErrorCodes.Validation,
                "Validation failed",
                (IDictionary<string, string[]>?)validation.Errors
                    .GroupBy(e => JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName))
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())),
            DbUpdateException { InnerException: PostgresException pg } => MapPostgres(pg),
            PostgresException pg => MapPostgres(pg),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested =>
                (499, "request_cancelled", "Request cancelled", null),
            BadHttpRequestException bad => (bad.StatusCode, ErrorCodes.Validation, bad.Message, null),
            _ => (StatusCodes.Status500InternalServerError, ErrorCodes.Internal, "Internal server error", null),
        };

        if (exception is AppException)
            // Expected business outcomes (incl. 503 calls_disabled) are not server errors.
            logger.LogDebug("Request failed with {Code}: {Message}", code, exception.Message);
        else if (status >= 500)
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        else
            logger.LogDebug("Request failed with {Code}: {Message}", code, exception.Message);

        if (httpContext.Response.HasStarted)
            return true;

        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails { Status = status, Title = title };
        details.Extensions["code"] = code;
        if (errors is not null)
            details.Extensions["errors"] = errors;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }

    /// <summary>
    /// Safety net for database errors that services did not translate themselves (services should
    /// still handle expected races, e.g. re-read the row on a duplicate ClientMessageId).
    /// </summary>
    private static (int, string, string, IDictionary<string, string[]>?) MapPostgres(PostgresException pg) => pg.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => pg.ConstraintName switch
        {
            "ix_users_username_normalized" => (StatusCodes.Status409Conflict, ErrorCodes.UsernameTaken, "Username taken", null),
            "ix_users_phone_number" => (StatusCodes.Status409Conflict, ErrorCodes.PhoneTaken, "Phone number taken", null),
            _ => (StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Conflict", null),
        },
        PostgresErrorCodes.ForeignKeyViolation => (StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Referenced item not found", null),
        PostgresErrorCodes.StringDataRightTruncation => (StatusCodes.Status400BadRequest, ErrorCodes.Validation, "Value too long", null),
        PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected =>
            (StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Concurrent update, retry", null),
        _ => (StatusCodes.Status500InternalServerError, ErrorCodes.Internal, "Internal server error", null),
    };
}
