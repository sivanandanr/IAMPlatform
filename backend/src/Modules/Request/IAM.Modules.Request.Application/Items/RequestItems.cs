using IAM.Modules.Request.Domain.Items;

namespace IAM.Modules.Request.Application.Items;

/// <summary>
/// What a requester submits (DL-091): for whom, which entitlement, and either an existing account or a new one (its type, and its
/// name only for a Service or Shared account), with the days wanted when the entitlement lets the requester choose.
/// </summary>
public sealed record SubmitItem(string Beneficiary, Guid EntitlementId, Guid? AccountId, string? NewAccountType, string? NewAccountName, int? DurationDays);

public sealed record RequestItemView(
    Guid Id,
    string Requester,
    string Beneficiary,
    Guid ApplicationId,
    Guid EntitlementId,
    Guid EntitlementVersionId,
    Guid? AccountId,
    string? NewAccountType,
    string? NewAccountName,
    int? DurationDays,
    string State,
    Guid MatrixVersionId,
    Guid MatchedRuleId,
    DateTimeOffset SubmittedAtUtc);

/// <summary>
/// Request items (DL-091). Submitting checks everything server-side, freezes the item and sends it for approval in one
/// transaction (ADR-0006); every refusal is audited.
/// </summary>
public interface IRequestItems
{
    Task<RequestItemView> SubmitAsync(SubmitItem item, CancellationToken cancellationToken);

    /// <summary>The item, if the caller asked for it, gets the access, or may read every item; otherwise null.</summary>
    Task<RequestItemView?> FindAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Items the caller asked for or gets access from, newest first, at most 200.</summary>
    Task<IReadOnlyList<RequestItemView>> MineAsync(CancellationToken cancellationToken);
}

public enum RequestFailureKind
{
    None = 0,
    InvalidField = 1,
    Forbidden = 2,
    WrongState = 3,
    Invalid = 4,
    Conflict = 5,
    NotFound = 6,
    Unavailable = 7,
}

public static class RequestApi
{
    public static RequestFailureKind Classify(Exception exception, out string? code, out string? field, out string? rule)
    {
        code = null;
        field = null;
        rule = null;
        switch (exception)
        {
            case RequestRejectedException rejected:
                field = rejected.Field;
                rule = rejected.Message;
                return RequestFailureKind.InvalidField;
            case RequestConflictException:
                return RequestFailureKind.Conflict;
            case RequestRefusedException refused:
                code = refused.Refusal?.ToString();
                return refused.Refusal switch
                {
                    RequestRefusal.NotAllowed or RequestRefusal.NotForOthers or RequestRefusal.NotAccountOwner => RequestFailureKind.Forbidden,
                    RequestRefusal.AlreadyRequested or RequestRefusal.PrimaryExists => RequestFailureKind.WrongState,
                    RequestRefusal.InvalidRecord or null => RequestFailureKind.Unavailable,
                    _ => RequestFailureKind.Invalid,
                };
            default:
                return RequestFailureKind.None;
        }
    }
}

public sealed class RequestConflictException : Exception
{
    public RequestConflictException()
        : base("Changed by someone else; try again.")
    {
    }

    public RequestConflictException(string message)
        : base(message)
    {
    }

    public RequestConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
