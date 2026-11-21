using System.Globalization;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Workflow.Infrastructure.Approvals;

/// <summary>
/// The R-037 check (DL-093): replays the instance from its action log with the workflow login, exactly as the workflow does,
/// then reads the instance's audit trail with the audit login and requires, for every stored action, exactly one intact
/// <c>Succeeded</c> audit record of that action's kind and sequence carrying the action row's hash and version (DL-075a),
/// and no audited action the log doesn't have. A forgery now has to change two stores through two logins. Nothing is
/// recorded here: damage is the workflow's to record when it next loads the instance, and the caller reports a failure.
/// </summary>
public sealed class ApprovalIntegrityCheck(WorkflowDbContext db, IAuditTrail trail) : IApprovalIntegrity
{
    /// <summary>The most audit events one approval may have before the check refuses it (actions, refusals and damage).</summary>
    public const int MaxAuditEvents = 5_000;

    private const int Attempts = 3;

    private static readonly Dictionary<string, ApprovalActionKind> ActionCodes =
        Enum.GetValues<ApprovalActionKind>().ToDictionary(WorkflowAuditActions.For, k => k, StringComparer.Ordinal);

    public async Task<ApprovalIntegrityReport> CheckAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (instanceId == Guid.Empty)
        {
            return ApprovalIntegrityReport.Failed(instanceId, ApprovalIntegrityStatus.NotFound, "NotFound");
        }

        for (var attempt = 1; ; attempt++)
        {
            var report = await CheckOnceAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (report.Status != ApprovalIntegrityStatus.Changing || attempt == Attempts)
            {
                return report;
            }
        }
    }

    /// <summary>Null when the log and the audit agree, else a problem code.</summary>
    public static string? Compare(Guid instanceId, IReadOnlyList<ApprovalActionRow> actions, IReadOnlyList<AuditTrailEntry> trailEntries)
    {
        // Only the records of accepted actions: refusals and damage reports are audited against the instance too.
        var audited = trailEntries.Where(e => ActionCodes.ContainsKey(e.Action)).ToList();
        if (audited.Exists(e => !e.Intact))
        {
            return "AuditChanged";
        }

        var bySequence = new Dictionary<int, List<AuditTrailEntry>>();
        foreach (var entry in audited)
        {
            if (entry.Outcome != "Succeeded"
                || !entry.Payload.TryGetValue("sequence", out var text)
                || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                || sequence < 1)
            {
                return "AuditUnexpected";
            }

            if (!bySequence.TryGetValue(sequence, out var list))
            {
                bySequence[sequence] = list = [];
            }

            list.Add(entry);
        }

        if (bySequence.Keys.Any(s => s > actions.Count))
        {
            return "AuditUnexpected";
        }

        foreach (var action in actions)
        {
            if (!bySequence.TryGetValue(action.Sequence, out var records))
            {
                return "AuditMissing";
            }

            if (records.Count > 1)
            {
                return "AuditDuplicate";
            }

            var record = records[0];
            var kind = Enum.Parse<ApprovalActionKind>(action.Kind);
            var hash = ApprovalActionLog.RowHash(instanceId, action.Sequence, new LoggedAction(kind, action.Payload, action.AtUtc, action.Actor), action.VersionAfter);
            if (ActionCodes[record.Action] != kind
                || !record.Payload.TryGetValue("actionHash", out var recorded) || !string.Equals(recorded, hash, StringComparison.Ordinal)
                || !record.Payload.TryGetValue("versionAfter", out var version)
                || !string.Equals(version, action.VersionAfter.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return "HashMismatch";
            }
        }

        return null;
    }

    private async Task<ApprovalIntegrityReport> CheckOnceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var row = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return ApprovalIntegrityReport.Failed(instanceId, ApprovalIntegrityStatus.NotFound, "NotFound");
        }

        var actions = await db.Actions.AsNoTracking().Where(a => a.InstanceId == instanceId).OrderBy(a => a.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var history = await db.History.AsNoTracking().Where(h => h.InstanceId == instanceId).OrderBy(h => h.EventVersion).ToListAsync(cancellationToken).ConfigureAwait(false);
        ApprovalWorkflowService.Loaded loaded;
        try
        {
            loaded = ApprovalWorkflowService.Rebuild(row, actions, history);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A writer committing between the reads makes them disagree: that is a change, not damage.
            return await MovedAsync(row, cancellationToken).ConfigureAwait(false)
                ? ApprovalIntegrityReport.Failed(instanceId, ApprovalIntegrityStatus.Changing, "Changing")
                : ApprovalIntegrityReport.Failed(instanceId, ApprovalIntegrityStatus.Damaged, "DoesNotReplay");
        }

        IReadOnlyList<AuditTrailEntry> trailEntries;
        try
        {
            trailEntries = await trail.ForTargetAsync(WorkflowAuditActions.TargetType, instanceId.ToString("D"), MaxAuditEvents, cancellationToken).ConfigureAwait(false);
        }
        catch (AuditTrailTooLongException)
        {
            return ApprovalIntegrityReport.Failed(instanceId, ApprovalIntegrityStatus.Mismatch, "AuditTooLong");
        }

        if (Compare(instanceId, actions, trailEntries) is { } problem)
        {
            // An action committed after the log was read is audited but not in what was read: check again.
            return problem == "AuditUnexpected" && await MovedAsync(row, cancellationToken).ConfigureAwait(false)
                ? ApprovalIntegrityReport.Failed(instanceId, ApprovalIntegrityStatus.Changing, "Changing")
                : ApprovalIntegrityReport.Failed(instanceId, ApprovalIntegrityStatus.Mismatch, problem);
        }

        var binding = loaded.Instance.Binding;
        return new ApprovalIntegrityReport(
            instanceId,
            ApprovalIntegrityStatus.Verified,
            null,
            binding.RequestItemId,
            binding.Requester.Value,
            binding.Beneficiary.Value,
            loaded.Instance.ProvisioningAuthorized,
            loaded.Instance.Stages.Any(s => s.Kind == StageKind.Action
                && s.State is StageState.Pending or StageState.Active or StageState.AwaitingApprovers or StageState.Held),
            actions.Count,
            new ApprovedRoute(
                binding.PolicyVersionId,
                binding.PolicyHash,
                binding.MatchedRuleId,
                binding.PolicyMaker.Value,
                [.. loaded.Stages.Select(Approved)]));
    }

    private static ApprovedStage Approved(StageDefinition s) => new(
        s.StageId,
        s.Sequence,
        s.Kind.ToString(),
        s.Approvers.Kind.ToString(),
        s.Approvers.WorkgroupKey,
        s.Approvers.ApplicationId,
        s.Approvers.RoleTypeId,
        s.Approvers.EntitlementId,
        s.Quorum.ToString(),
        s.MinimumApprovals,
        s.Mandatory,
        s.Independent,
        (int)s.Deadline.TotalHours,
        s.OnTimeout.ToString());

    private async Task<bool> MovedAsync(ApprovalInstanceRow read, CancellationToken cancellationToken)
    {
        var now = await db.Instances.AsNoTracking()
            .Where(i => i.Id == read.Id)
            .Select(i => new { i.ActionCount, i.EngineVersion })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return now is not null && (now.ActionCount != read.ActionCount || now.EngineVersion != read.EngineVersion);
    }
}
