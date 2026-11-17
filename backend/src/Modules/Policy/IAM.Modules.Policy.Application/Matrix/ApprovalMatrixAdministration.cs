using IAM.Modules.Policy.Domain.Matrix;

namespace IAM.Modules.Policy.Application.Matrix;

public sealed record MatrixOverlapView(Guid FirstRuleId, Guid SecondRuleId, int Priority);

/// <summary>One matrix version. <c>Document</c> is the canonical stored JSON; <c>ConcurrencyVersion</c> is sent back when editing.</summary>
public sealed record MatrixVersionView(
    Guid VersionId,
    int VersionNumber,
    string State,
    string Document,
    string ContentHash,
    string CompiledHash,
    string EditedBy,
    IReadOnlyList<MatrixOverlapView> Overlaps,
    bool OverlapCheckTruncated,
    IReadOnlyList<Guid> NeverTrueRuleIds,
    long ConcurrencyVersion);

public sealed record ApprovalMatrixView(Guid Id, MatrixVersionView? Active, MatrixVersionView? Draft, long ConcurrencyVersion);

public sealed record MatrixChangeRequestView(
    Guid Id, int VersionNumber, string ContentHash, string CompiledHash, string Maker, string Reason, DateTimeOffset ProposedAtUtc,
    string State, string? Checker, string? DecisionReason, DateTimeOffset? DecidedAtUtc);

public sealed record MatrixChangeReview(MatrixChangeRequestView Request, MatrixVersionView? Draft);

public sealed record BuiltInFactView(Guid AttributeId, string Key, string DisplayName, string Subject, string DataType, IReadOnlyList<string> Operators);

/// <summary>
/// Approval matrix administration (DL-090). Every write checks the caller's permission and is audited in the same transaction,
/// refusals included. A draft is compiled on every save; a version with possible overlaps can't be proposed, and a second person
/// who neither proposed nor wrote it activates it.
/// </summary>
public interface IApprovalMatrixAdministration
{
    Task<ApprovalMatrixView?> FindAsync(CancellationToken cancellationToken);

    Task<ApprovalMatrixView> SetDraftAsync(long? expectedConcurrencyVersion, string document, CancellationToken cancellationToken);

    Task<MatrixChangeRequestView> ProposeAsync(string reason, CancellationToken cancellationToken);

    Task<MatrixChangeReview?> ReviewAsync(Guid requestId, CancellationToken cancellationToken);

    Task<MatrixChangeRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken);

    Task<MatrixChangeRequestView> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken);

    Task<MatrixChangeRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    Task<IReadOnlyList<BuiltInFactView>> ListFactsAsync(CancellationToken cancellationToken);
}

public enum MatrixFailureKind
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

public static class MatrixApi
{
    public static MatrixFailureKind Classify(Exception exception, out string? code, out string? field, out string? rule)
    {
        code = null;
        field = null;
        rule = null;
        switch (exception)
        {
            case MatrixRejectedException rejected:
                field = rejected.Field;
                rule = rejected.Message;
                return MatrixFailureKind.InvalidField;
            case MatrixConflictException:
                return MatrixFailureKind.Conflict;
            case MatrixNotFoundException:
                return MatrixFailureKind.NotFound;
            case MatrixRefusedException refused:
                code = refused.Refusal?.ToString();
                return refused.Refusal switch
                {
                    MatrixRefusal.NotAllowed or MatrixRefusal.MakerCannotDecide or MatrixRefusal.NotMaker or MatrixRefusal.MakerNoLongerAllowed => MatrixFailureKind.Forbidden,
                    MatrixRefusal.NotProposed or MatrixRefusal.AlreadyPending or MatrixRefusal.NoDraft or MatrixRefusal.DraftChanged or MatrixRefusal.AttributesChanged => MatrixFailureKind.WrongState,
                    MatrixRefusal.InvalidRecord or null => MatrixFailureKind.Unavailable,
                    _ => MatrixFailureKind.Invalid,
                };
            default:
                return MatrixFailureKind.None;
        }
    }
}

public sealed class MatrixNotFoundException : Exception
{
    public MatrixNotFoundException()
        : base("The approval matrix or request was not found.")
    {
    }

    public MatrixNotFoundException(string message)
        : base(message)
    {
    }

    public MatrixNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MatrixConflictException : Exception
{
    public MatrixConflictException()
        : base("Changed by someone else; reload and try again.")
    {
    }

    public MatrixConflictException(string message)
        : base(message)
    {
    }

    public MatrixConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
