using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Infrastructure.Access;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IAM.Modules.Administration.Endpoints;

/// <summary>
/// Turns access-control outcomes into ProblemDetails (BACKEND_INPUT_VALIDATION): a refusal code at most, never a person,
/// a role name, a reason or a database message (R-033). A damaged store is a 503 and a logged error, never a grant.
/// </summary>
internal sealed partial class AccessExceptionHandler(IProblemDetailsService problems, ILogger<AccessExceptionHandler> logger, AccessIntegrityAuditThrottle logThrottle) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var kind = AccessFailures.Classify(exception, out var code);
        var (status, title) = kind switch
        {
            AccessFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "Access control is unavailable."),
            AccessFailureKind.Conflict => (StatusCodes.Status409Conflict, "Changed by someone else; reload and try again."),
            AccessFailureKind.NotFound => (StatusCodes.Status404NotFound, "No such access change request."),
            AccessFailureKind.Forbidden => (StatusCodes.Status403Forbidden, "The access action was refused."),
            AccessFailureKind.WrongState => (StatusCodes.Status409Conflict, "The access action was refused."),
            AccessFailureKind.Invalid => (StatusCodes.Status422UnprocessableEntity, "The access action was refused."),
            _ => (0, string.Empty),
        };
        if (status == 0)
        {
            return false;
        }

        if (exception is AccessStoreIntegrityException integrity
            && logThrottle.TryEnter($"log:{integrity.Check}:{integrity.AuditFailure is not null}", out var leftOut))
        {
            // The check code only: the exception chain can hold stored values. At most once a minute per check.
            LogIntegrityFailure(logger, integrity.Check ?? "unknown", integrity.AuditFailure is not null, leftOut);
        }

        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails { Status = status, Title = title };
        if (code is not null)
        {
            details.Extensions["code"] = code;
        }

        // Handled even when the caller's Accept header rules out a ProblemDetails body: the status code still stands, so the
        // exception never reaches a handler that might log it in full.
        await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = details, Exception = null })
            .ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Stored access control failed its integrity check ({Check}); every access check fails closed until it is repaired. Audit write failed: {AuditFailed}. Repeats left out: {LeftOut}.")]
    private static partial void LogIntegrityFailure(ILogger logger, string check, bool auditFailed, int leftOut);
}
