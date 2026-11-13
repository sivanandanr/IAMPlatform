using System.Diagnostics;
using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Administration.Infrastructure.Access;

/// <summary>
/// Audits permission-check denials (ADR-0008 "authorization denials audited", DL-063b): the person is the event's actor
/// (from the token), the target is the action id, and the payload the route template only, never the request. At most one
/// event per person and action per minute; the next one says how many were left out.
/// </summary>
public sealed class AccessDenialRecorder(AdministrationDbContext db, IAuditWriter audit, TimeProvider clock, AccessDenialAuditThrottle throttle)
    : IPermissionDenialRecorder
{
    public async Task RecordAsync(string subject, string action, string? routeTemplate, CancellationToken cancellationToken)
    {
        if (!throttle.TryEnter($"{subject}\n{action}", out var suppressed))
        {
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = new AuditEventRecord
        {
            Action = AccessAuditActions.Denied,
            TargetType = AccessAuditActions.ActionTargetType,
            TargetId = action,
            Outcome = AuditOutcome.Denied,
            OccurredAtUtc = UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow()),
            CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
            Payload =
            [
                new AuditPayloadField("route", routeTemplate ?? "unknown", AuditFieldClass.Plain),
                new AuditPayloadField("suppressedSincePrevious", suppressed.ToString(CultureInfo.InvariantCulture), AuditFieldClass.Plain),
            ],
        };
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
