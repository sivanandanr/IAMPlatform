using System.Data.Common;

namespace IAM.Modules.Audit.Contracts;

/// <summary>
/// Appends audit events to <c>audit.AuditEvent</c> inside the caller's own database transaction (ADR-0018, DL-022):
/// the event and the business change are committed together or rolled back together, so an acknowledged change never
/// lacks its audit record (IAM-FR-027). The acting identity is taken from the server-side actor, never from the record.
/// Modules call this contract; they never write the audit table themselves.
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Validates, masks and appends <paramref name="record"/> using <paramref name="transaction"/>, which must be open on
    /// the IAM database. If anything fails (an invalid record, a database error, a timeout or cancellation), the writer
    /// <b>rolls back the caller's transaction</b> before rethrowing, so the business change can never be committed without
    /// its audit row. A rejected record throws <see cref="AuditRecordRejectedException"/>.
    /// </summary>
    Task<AuditWriteReceipt> AppendAsync(DbTransaction transaction, AuditEventRecord record, CancellationToken cancellationToken);
}

/// <summary>One audited action. Every text field is bounded; codes follow the rules in <c>AuditEventValidator</c>.</summary>
public sealed record AuditEventRecord
{
    /// <summary>Registered action code, for example <c>identity.session.started</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Kind of object acted on, for example <c>identity.user</c>.</summary>
    public required string TargetType { get; init; }

    /// <summary>Identifier of the object acted on. Must not contain personal data beyond the identifier itself.</summary>
    public string? TargetId { get; init; }

    public required AuditOutcome Outcome { get; init; }

    /// <summary>When the action happened (UTC). The writer records its own <c>RecordedAtUtc</c> as well.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Correlation identifier of the request or message that caused the action.</summary>
    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    /// <summary>Subject the actor acted for (delegation), if different from the actor.</summary>
    public string? EffectiveActorId { get; init; }

    /// <summary>Business justification entered by a person, if any.</summary>
    public string? Reason { get; init; }

    /// <summary>Policy or approval-matrix version the decision used.</summary>
    public string? PolicyVersionRef { get; init; }

    /// <summary>Lower-case hex SHA-256 of that policy version.</summary>
    public string? PolicyVersionHash { get; init; }

    /// <summary>Version of this record's payload layout, starting at 1.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Extra detail. Sensitive fields are masked before the payload is stored and hashed.</summary>
    public IReadOnlyList<AuditPayloadField> Payload { get; init; } = [];
}

public enum AuditOutcome
{
    Succeeded = 1,
    Denied = 2,
    Failed = 3,
}

/// <summary>How a payload value is stored.</summary>
public enum AuditFieldClass
{
    /// <summary>Stored as given.</summary>
    Plain = 1,

    /// <summary>Replaced by a fixed mask before storing and hashing; the original value is never written.</summary>
    Sensitive = 2,
}

/// <summary>
/// One payload entry. The classification is required, so a caller must decide whether a value may be stored in clear.
/// Field names that suggest secrets or tokens are rejected, whatever the class.
/// </summary>
public sealed record AuditPayloadField(string Name, string? Value, AuditFieldClass Classification);

/// <summary>What was stored: the event identifier, the writer's timestamp and the whole-row checksum.</summary>
public sealed record AuditWriteReceipt(Guid EventId, DateTimeOffset RecordedAtUtc, string RowHash);

/// <summary>
/// Declares an action code a module may audit. Modules register these in DI; the writer refuses unregistered actions.
/// All registrations are checked once when the action catalog is first created.
/// </summary>
public sealed record AuditActionRegistration(string Action);

/// <summary>
/// The record was refused and nothing was written. The message names the field and rule only, never the value, so a
/// rejected secret is not echoed into logs.
/// </summary>
public sealed class AuditRecordRejectedException : Exception
{
    public AuditRecordRejectedException()
    {
    }

    public AuditRecordRejectedException(string message)
        : base(message)
    {
    }

    public AuditRecordRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AuditRecordRejectedException(string field, string rule)
        : base($"Audit record rejected: {field} {rule}.")
    {
        Field = field;
    }

    public string? Field { get; }
}
