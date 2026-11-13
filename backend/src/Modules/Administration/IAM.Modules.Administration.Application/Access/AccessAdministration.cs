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

    /// <summary>
    /// Stores the default roles and the first two people, once (DL-059e). Refused when anything is stored already.
    /// <paramref name="evidence"/> is recorded in the audit event (DL-063a).
    /// </summary>
    Task BootstrapAsync(string administrator, string manager, BootstrapEvidence? evidence, CancellationToken cancellationToken);
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

/// <summary>How a caller-facing layer should treat a failure from access control, without naming domain types.</summary>
public enum AccessFailureKind
{
    /// <summary>Not an access-control failure.</summary>
    None = 0,

    /// <summary>Who the caller is or what they hold rules it out (not allowed, self-grant, own change, no longer allowed).</summary>
    Forbidden = 1,

    /// <summary>The request is not in a state that allows this (already decided, already set up).</summary>
    WrongState = 2,

    /// <summary>The change itself breaks a rule (unknown role, incompatible roles, lockout, invalid text...).</summary>
    Invalid = 3,

    /// <summary>Someone else changed the same thing first.</summary>
    Conflict = 4,

    /// <summary>No such change request.</summary>
    NotFound = 5,

    /// <summary>The stored access data failed its integrity check: fail closed.</summary>
    Unavailable = 6,
}

public static class AccessFailures
{
    /// <summary>Classifies <paramref name="exception"/>; <paramref name="code"/> is the refusal code (an enum name, never data), if any.</summary>
    public static AccessFailureKind Classify(Exception exception, out string? code)
    {
        code = null;
        switch (exception)
        {
            case AccessStoreIntegrityException:
                return AccessFailureKind.Unavailable;
            case AccessConflictException:
                return AccessFailureKind.Conflict;
            case AccessChangeNotFoundException:
                return AccessFailureKind.NotFound;
            case AccessRefusedException refused:
                code = refused.Refusal?.ToString();
                return refused.Refusal switch
                {
                    AccessRefusal.NotAllowed or AccessRefusal.SelfGrant or AccessRefusal.MakerCannotDecide or AccessRefusal.NotMaker
                        or AccessRefusal.MakerNoLongerAllowed => AccessFailureKind.Forbidden,
                    AccessRefusal.NotProposed or AccessRefusal.AlreadyBootstrapped => AccessFailureKind.WrongState,
                    _ => AccessFailureKind.Invalid,
                };
            default:
                return AccessFailureKind.None;
        }
    }
}

/// <summary>One person's own roles and actions at a model version (role codes and action ids only).</summary>
public sealed record OwnPermissionsView(IReadOnlyList<string> Roles, IReadOnlyList<string> Actions, long ModelVersion);

public static class AccessQueries
{
    /// <summary>The roles and actions <paramref name="subject"/> has in the current model, actions in ordinal order.</summary>
    public static async Task<OwnPermissionsView> OwnPermissionsAsync(this IAccessAdministration access, string subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        var model = await access.CurrentModelAsync(cancellationToken).ConfigureAwait(false);
        return new OwnPermissionsView(model.RolesOf(subject), [.. model.ActionsOf(subject).Order(StringComparer.Ordinal)], model.Version);
    }
}

/// <summary>
/// Who ran the one-time bootstrap and on whose approval (DL-063a): the approval or ticket reference, and the operating
/// system user and host the tool ran as. Recorded in the bootstrap audit event; not checked against anything.
/// </summary>
public sealed record BootstrapEvidence(string ApprovalReference, string OperatingSystemUser, string Host);

