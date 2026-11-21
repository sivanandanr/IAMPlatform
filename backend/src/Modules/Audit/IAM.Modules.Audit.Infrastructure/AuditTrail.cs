using System.Text.Json;
using IAM.Modules.Audit.Application;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Audit.Infrastructure;

/// <summary>
/// Reads one target's audit events with the audit login (DL-093) and recomputes each row's hash from its stored columns, so
/// a row changed after it was written reads as not intact. The payload is the stored canonical JSON object of strings.
/// </summary>
public sealed class AuditTrail(AuditDbContext db) : IAuditTrail
{
    public const int MaxEvents = 10_000;

    public async Task<IReadOnlyList<AuditTrailEntry>> ForTargetAsync(string targetType, string targetId, int max, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetType);
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxEvents);
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.TargetType == targetType && e.TargetId == targetId)
            .OrderBy(e => e.Seq)
            .Take(max + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count > max)
        {
            throw new AuditTrailTooLongException("The target has more audit events than allowed.");
        }

        return [.. rows.Select(r => new AuditTrailEntry(r.Id, r.Seq, r.Action, r.Outcome, Payload(r.PayloadCanonical, out var parsed), parsed && Intact(r)))];
    }

    private static bool Intact(AuditEventRow r) =>
        string.Equals(
            r.RowHash,
            AuditEventValidator.ComputeRowHash(new ValidatedAuditEvent(
                r.Id, r.RecordedAtUtc, r.OccurredAtUtc, r.ActorId, r.EffectiveActorId, r.Action, r.TargetType, r.TargetId, r.Outcome, r.Reason,
                r.CorrelationId, r.CausationId, r.PolicyVersionRef, r.PolicyVersionHash, r.PayloadCanonical, r.SchemaVersion)),
            StringComparison.Ordinal);

    /// <summary>The payload's fields, or none (and <paramref name="parsed"/> false) if it isn't a flat object of strings.</summary>
    private static Dictionary<string, string?> Payload(string canonical, out bool parsed)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        parsed = false;
        try
        {
            using var document = JsonDocument.Parse(canonical, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return fields;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || !fields.TryAdd(property.Name, property.Value.GetString()))
                {
                    fields.Clear();
                    return fields;
                }
            }

            parsed = true;
            return fields;
        }
        catch (JsonException)
        {
            return fields;
        }
    }
}
