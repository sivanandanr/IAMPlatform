using IAM.Modules.Administration.Domain.Access;

namespace IAM.Modules.Administration.Application.Access;

/// <summary>
/// Stored access control: the current model and its change requests (stage 2, P0-013, DL-059). Who is acting always
/// comes from the server-side actor, never from an argument, so a caller can't act as someone else. Each change and its
/// audit event are saved in one transaction (ADR-0018); refusals are audited too.
/// </summary>
public interface IAccessAdministration
{
    /// <summary>The current model. Before the first administrators are set, nobody holds any role.</summary>
    Task<AccessModel> CurrentModelAsync(CancellationToken cancellationToken);

    Task<ChangeRequestView?> FindChangeAsync(Guid id, CancellationToken cancellationToken);

    Task<ChangeRequestView> ProposeAsync(Guid id, AccessChange change, string reason, CancellationToken cancellationToken);

    Task<ChangeRequestView> ApproveAsync(Guid id, string? reason, CancellationToken cancellationToken);

    Task<ChangeRequestView> RejectAsync(Guid id, string reason, CancellationToken cancellationToken);

    Task<ChangeRequestView> WithdrawAsync(Guid id, string? reason, CancellationToken cancellationToken);

    /// <summary>Stores the default roles and the first two people, once (DL-059e). Refused when anything is stored already.</summary>
    Task BootstrapAsync(string administrator, string manager, CancellationToken cancellationToken);
}

/// <summary>A change request as stored.</summary>
public sealed record ChangeRequestView(
    Guid Id,
    AccessChange Change,
    string Maker,
    string Reason,
    DateTimeOffset ProposedAtUtc,
    long ProposedAgainstVersion,
    ChangeState State,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc,
    long? AppliedAsVersion);

/// <summary>Someone else changed the same thing first. Nothing was saved; reload and try again.</summary>
public sealed class AccessConflictException : Exception
{
    public AccessConflictException()
        : base("The access model or change request was changed by someone else; reload it and try again.")
    {
    }

    public AccessConflictException(string message)
        : base(message)
    {
    }

    public AccessConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AccessChangeNotFoundException : Exception
{
    public AccessChangeNotFoundException()
        : base("No such access change request.")
    {
    }

    public AccessChangeNotFoundException(string message)
        : base(message)
    {
    }

    public AccessChangeNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The stored access model or a stored change request is not something the rules allow, or no longer matches the model
/// version it claims to be. This means the database was changed outside the application, or there is a bug. It is
/// audited as a failure and must be treated as a server error and an alert, never as an ordinary refusal.
/// </summary>
public sealed class AccessStoreIntegrityException : Exception
{
    public AccessStoreIntegrityException()
        : base("The stored access control data failed its integrity check.")
    {
    }

    public AccessStoreIntegrityException(string message)
        : base(message)
    {
    }

    public AccessStoreIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AccessStoreIntegrityException(string check, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Check = check;
    }

    /// <summary>Which check failed: <c>rule</c>, <c>hash</c>, <c>link</c> or <c>change</c>. A code, never stored data.</summary>
    public string? Check { get; init; }

    /// <summary>Set when recording this failure in the audit log failed too. The integrity failure is still what is reported.</summary>
    public Exception? AuditFailure { get; init; }
}
