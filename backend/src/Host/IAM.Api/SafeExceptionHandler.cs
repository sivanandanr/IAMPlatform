using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace IAM.Api;

/// <summary>
/// Last resort, after every module's handler (R-033, BACKEND_INPUT_VALIDATION): any other failure is a 500 ProblemDetails
/// with no detail, and the log gets the exception type only. Database messages can hold people and role codes, so the
/// exception itself is never written out. Handling it here also stops the framework logging it in full.
/// </summary>
internal sealed partial class SafeExceptionHandler(IProblemDetailsService problems, ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception.GetType().FullName ?? exception.GetType().Name, httpContext.TraceIdentifier);
        }

        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails { Status = status, Title = status >= 500 ? "An unexpected error occurred." : "The request could not be processed." };
        await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = details, Exception = null }).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled {ExceptionType} (trace {TraceId}); details withheld from logs (R-033).")]
    private static partial void LogUnhandled(ILogger logger, string exceptionType, string traceId);
}
