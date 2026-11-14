using IAM.Modules.Organization.Domain.Workgroups;

namespace IAM.Modules.Organization.Application.Workgroups;

public sealed record RegisterWorkgroupRequest(string Key, string DisplayName, IReadOnlyList<string?> Members);

/// <summary>One member list version. <c>ConcurrencyVersion</c> is the token to send back when editing a draft.</summary>
public sealed record WorkgroupVersionView(Guid VersionId, int VersionNumber, string State, IReadOnlyList<string> Members, string ContentHash, long ConcurrencyVersion);

public sealed record WorkgroupView(Guid Id, string Key, string DisplayName, bool IsActive, WorkgroupVersionView? Active, WorkgroupVersionView? Draft, long ConcurrencyVersion);

public sealed record WorkgroupChangeRequestView(
    Guid Id,
    Guid WorkgroupId,
    int VersionNumber,
    string ContentHash,
    string Maker,
    string Reason,
    DateTimeOffset ProposedAtUtc,
    string State,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc);

/// <summary>A request with the draft it would activate, if that draft is still the one named, with the content named.</summary>
public sealed record WorkgroupChangeReview(WorkgroupChangeRequestView Request, WorkgroupVersionView? Draft);

/// <summary>
/// Workgroup administration (DL-068). Every call checks its permission; every refused write is audited; activating a
/// member list needs a second person who is not the maker, an author of the draft or a member of it.
/// </summary>
public interface IWorkgroupAdministration
{
    Task<WorkgroupView> RegisterAsync(RegisterWorkgroupRequest request, CancellationToken cancellationToken);

    Task<WorkgroupView> RenameAsync(Guid workgroupId, long expectedConcurrencyVersion, string displayName, CancellationToken cancellationToken);

    Task<WorkgroupView> UpdateDraftAsync(Guid workgroupId, long expectedConcurrencyVersion, IReadOnlyList<string?> members, CancellationToken cancellationToken);

    Task<WorkgroupView> StartNewDraftAsync(Guid workgroupId, IReadOnlyList<string?> members, CancellationToken cancellationToken);

    Task<WorkgroupView?> FindAsync(string key, CancellationToken cancellationToken);

    Task<WorkgroupChangeRequestView> ProposeAsync(Guid workgroupId, string reason, CancellationToken cancellationToken);

    Task<WorkgroupChangeRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken);

    Task<WorkgroupChangeRequestView> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken);

    Task<WorkgroupChangeRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    Task<WorkgroupChangeReview?> ReviewRequestAsync(Guid requestId, CancellationToken cancellationToken);
}

public sealed class WorkgroupNotFoundException : Exception
{
    public WorkgroupNotFoundException()
        : base("The workgroup or request was not found.")
    {
    }

    public WorkgroupNotFoundException(string message)
        : base(message)
    {
    }

    public WorkgroupNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WorkgroupConflictException : Exception
{
    public WorkgroupConflictException()
        : base("The workgroup was changed by someone else; reload it and try again.")
    {
    }

    public WorkgroupConflictException(string message)
        : base(message)
    {
    }

    public WorkgroupConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>How a caller-facing layer should treat a workgroup failure, without naming domain types.</summary>
public enum WorkgroupFailureKind
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

public static class WorkgroupApi
{
    /// <summary>Classifies a workgroup failure. <paramref name="code"/> is an enum name and <paramref name="field"/> a field name, never data.</summary>
    public static WorkgroupFailureKind Classify(Exception exception, out string? code, out string? field, out string? rule)
    {
        code = null;
        field = null;
        rule = null;
        switch (exception)
        {
            case WorkgroupRejectedException rejected:
                field = rejected.Field;
                rule = rejected.Message;
                return WorkgroupFailureKind.InvalidField;
            case WorkgroupConflictException:
                return WorkgroupFailureKind.Conflict;
            case WorkgroupNotFoundException:
                return WorkgroupFailureKind.NotFound;
            case WorkgroupRefusedException refused:
                code = refused.Refusal?.ToString();
                return refused.Refusal switch
                {
                    WorkgroupRefusal.NotAllowed or WorkgroupRefusal.MakerCannotDecide or WorkgroupRefusal.MemberCannotDecide
                        or WorkgroupRefusal.NotMaker or WorkgroupRefusal.MakerNoLongerAllowed => WorkgroupFailureKind.Forbidden,
                    WorkgroupRefusal.NotProposed or WorkgroupRefusal.AlreadyPending or WorkgroupRefusal.DraftExists
                        or WorkgroupRefusal.NoDraft or WorkgroupRefusal.DraftChanged => WorkgroupFailureKind.WrongState,
                    WorkgroupRefusal.InvalidRecord or null => WorkgroupFailureKind.Unavailable,
                    _ => WorkgroupFailureKind.Invalid,
                };
            default:
                return WorkgroupFailureKind.None;
        }
    }
}
