using SkyMessage.Contracts;

namespace SkyMessage.Application.Common.Exceptions;

/// <summary>
/// Expected business errors. The Api exception handler turns them into problem details:
/// status = <see cref="StatusCode"/>, extension "code" = <see cref="Code"/> (one of <see cref="ErrorCodes"/>).
/// </summary>
public class AppException(string code, string message, int statusCode, IDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public IDictionary<string, string[]>? Errors { get; } = errors;
}

public sealed class BadRequestException(string code, string? message = null, IDictionary<string, string[]>? errors = null)
    : AppException(code, message ?? code, 400, errors);

public sealed class UnauthorizedException(string code = ErrorCodes.Unauthorized, string? message = null)
    : AppException(code, message ?? code, 401);

public sealed class ForbiddenException(string code = ErrorCodes.Forbidden, string? message = null)
    : AppException(code, message ?? code, 403);

public sealed class NotFoundException(string code = ErrorCodes.NotFound, string? message = null)
    : AppException(code, message ?? code, 404);

public sealed class ConflictException(string code = ErrorCodes.Conflict, string? message = null)
    : AppException(code, message ?? code, 409);

public sealed class RateLimitedException(string? message = null)
    : AppException(ErrorCodes.RateLimited, message ?? ErrorCodes.RateLimited, 429);
