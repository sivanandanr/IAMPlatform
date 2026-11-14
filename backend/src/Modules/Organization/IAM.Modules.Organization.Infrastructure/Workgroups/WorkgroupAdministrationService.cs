using System.Diagnostics;
using System.Globalization;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Organization.Application.Workgroups;
using IAM.Modules.Organization.Contracts;
using IAM.Modules.Organization.Domain.Workgroups;
using IAM.Modules.Organization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Organization.Infrastructure.Workgroups;

/// <summary>Audit action codes of workgroups (registered with the audit writer by the Organization module).</summary>
public static class WorkgroupAuditActions
{
    public const string Registered = "organization.workgroup.registered";
    public const string Renamed = "organization.workgroup.renamed";
    public const string DraftUpdated = "organization.workgroup.draft_updated";
    public const string NewDraftStarted = "organization.workgroup.new_draft_started";
    public const string ChangeProposed = "organization.workgroup.change_proposed";
    public const string ChangeApproved = "organization.workgroup.change_approved";
    public const string ChangeRejected = "organization.workgroup.change_rejected";
    public const string ChangeWithdrawn = "organization.workgroup.change_withdrawn";
    public const string ChangeRefused = "organization.workgroup.change_refused";
    public const string TargetType = "organization.workgroup";

    public const string ReadAction = AccessActions.WorkgroupsRead;
    public const string ManageAction = AccessActions.WorkgroupsManage;
    public const string ApproveAction = AccessActions.WorkgroupsApprove;

    public static IReadOnlyList<string> All { get; } =
        [Registered, Renamed, DraftUpdated, NewDraftStarted, ChangeProposed, ChangeApproved, ChangeRejected, ChangeWithdrawn, ChangeRefused];
}

/// <summary>
/// Workgroup administration (DL-068), built like the application catalog (DL-066, DL-067): each change and its audit
/// event in one transaction; every refused write audited, including a missing permission; permissions checked here as well
/// as at the endpoint; edits and proposals serialised on the workgroup row; approvals saved in order (<see cref="OrderedSave"/>).
/// </summary>
public sealed class WorkgroupAdministrationService(
    OrganizationDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions) : IWorkgroupAdministration
{
    private const string ProposedState = nameof(SecondPersonState.Proposed);

    public async Task<WorkgroupView> RegisterAsync(RegisterWorkgroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Workgroup workgroup;
        try
        {
            await RequireAsync(WorkgroupAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            workgroup = Workgroup.Register(request.Key, request.DisplayName, request.Members);
        }
        catch (WorkgroupRefusedException refusal)
        {
            await RefuseAsync(tx, null, WorkgroupRules.IsKey(request.Key) ? request.Key : "(invalid)", "register", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (await db.Workgroups.AnyAsync(w => w.Key == workgroup.Key, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkgroupRejectedException("Key", "is already registered");
        }

        db.Workgroups.Add(workgroup);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            throw new WorkgroupRejectedException("Key", "is already registered", ex);
        }

        await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(WorkgroupAuditActions.Registered, workgroup, workgroup.CurrentDraft!), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(workgroup);
    }

    /// <summary>A name is not access, but it is frozen while a change is pending, so a reviewer sees what they approve.</summary>
    public async Task<WorkgroupView> RenameAsync(Guid workgroupId, long expectedConcurrencyVersion, string displayName, CancellationToken cancellationToken)
    {
        var workgroup = await LoadAsync(workgroupId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string previous;
        bool changed;
        try
        {
            await RequireAsync(WorkgroupAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(workgroupId, cancellationToken).ConfigureAwait(false);
            if (db.Entry(workgroup).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
            {
                throw new WorkgroupConflictException();
            }

            previous = workgroup.DisplayName;
            changed = workgroup.Rename(displayName);
        }
        catch (WorkgroupRefusedException refusal)
        {
            await RefuseAsync(tx, workgroup, "rename", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(
                tx.GetDbTransaction(),
                Event(WorkgroupAuditActions.Renamed, workgroup.Id, AuditOutcome.Succeeded, null, Field("key", workgroup.Key), Field("previousDisplayName", previous), Field("displayName", workgroup.DisplayName)),
                cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return View(workgroup);
    }

    public async Task<WorkgroupView> UpdateDraftAsync(Guid workgroupId, long expectedConcurrencyVersion, IReadOnlyList<string?> members, CancellationToken cancellationToken)
    {
        var workgroup = await LoadAsync(workgroupId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        WorkgroupVersion draft;
        bool changed;
        try
        {
            await RequireAsync(WorkgroupAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(workgroupId, cancellationToken).ConfigureAwait(false);
            draft = workgroup.CurrentDraft ?? throw new WorkgroupRefusedException(WorkgroupRefusal.NoDraft);
            if (db.Entry(draft).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
            {
                throw new WorkgroupConflictException();
            }

            changed = workgroup.UpdateDraft(members);
        }
        catch (WorkgroupRefusedException refusal)
        {
            await RefuseAsync(tx, workgroup, "update_draft", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            // Edits and proposals both bump the workgroup row, so whichever of two racing ones commits second fails.
            TouchWorkgroup(workgroup);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(WorkgroupAuditActions.DraftUpdated, workgroup, draft), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return View(workgroup);
    }

    public async Task<WorkgroupView> StartNewDraftAsync(Guid workgroupId, IReadOnlyList<string?> members, CancellationToken cancellationToken)
    {
        var workgroup = await LoadAsync(workgroupId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        WorkgroupVersion draft;
        try
        {
            await RequireAsync(WorkgroupAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            var highest = await db.WorkgroupVersions.Where(v => v.WorkgroupId == workgroupId).MaxAsync(v => v.VersionNumber, cancellationToken).ConfigureAwait(false);
            draft = workgroup.StartNewDraft(members, highest);
        }
        catch (WorkgroupRefusedException refusal)
        {
            await RefuseAsync(tx, workgroup, "start_draft", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(WorkgroupAuditActions.NewDraftStarted, workgroup, draft), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(workgroup);
    }

    public async Task<WorkgroupView?> FindAsync(string key, CancellationToken cancellationToken)
    {
        await RequireAsync(WorkgroupAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        WorkgroupRules.ValidateKey(key, "Key");
        var workgroup = await Current(db.Workgroups).SingleOrDefaultAsync(w => w.Key == key, cancellationToken).ConfigureAwait(false);
        return workgroup is null ? null : View(workgroup);
    }

    public async Task<WorkgroupChangeRequestView> ProposeAsync(Guid workgroupId, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var workgroup = await LoadAsync(workgroupId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        WorkgroupChangeRequest proposed;
        try
        {
            await RequireAsync(WorkgroupAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(workgroupId, cancellationToken).ConfigureAwait(false);
            proposed = WorkgroupChangeRequest.Propose(Guid.CreateVersion7(), workgroup, actor, reason, Now());
        }
        catch (WorkgroupRefusedException refusal)
        {
            await RefuseAsync(tx, workgroup, "propose", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.ChangeRequests.Add(new WorkgroupChangeRequestRow
        {
            Id = proposed.Id,
            WorkgroupId = proposed.WorkgroupId,
            VersionId = proposed.VersionId,
            VersionNumber = proposed.VersionNumber,
            ContentHash = proposed.ContentHash,
            Maker = proposed.Maker,
            Reason = proposed.Reason,
            ProposedAtUtc = proposed.ProposedAtUtc,
            State = proposed.State.ToString(),
        });
        TouchWorkgroup(workgroup);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(WorkgroupAuditActions.ChangeProposed, workgroup, proposed, proposed.Reason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(proposed);
    }

    public async Task<WorkgroupChangeRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var workgroup = await LoadAsync(row.WorkgroupId, cancellationToken).ConfigureAwait(false);
        var superseding = workgroup.CurrentActive?.VersionNumber;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        WorkgroupChangeRequest request;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor, workgroup);
            await RequireAsync(WorkgroupAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            await RequirePersonAsync(request.Maker, WorkgroupAuditActions.ManageAction, WorkgroupRefusal.MakerNoLongerAllowed, cancellationToken).ConfigureAwait(false);
            if (workgroup.CurrentDraft is { } draft && IsAuthorOf(draft, actor))
            {
                throw new WorkgroupRefusedException(WorkgroupRefusal.MakerCannotDecide);
            }

            if (!string.Equals(reviewedContentHash, request.ContentHash, StringComparison.Ordinal))
            {
                throw new WorkgroupRefusedException(WorkgroupRefusal.DraftChanged);
            }

            request.Approve(actor, reason, workgroup, Now());
        }
        catch (WorkgroupRefusedException refusal)
        {
            await RefuseAsync(tx, workgroup, "approve", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var activated = workgroup.Versions.Single(v => v.ActivatedByRequestId == request.Id);
        Apply(row, request);
        await db.SaveVersionsInOrderAsync(activated, SaveAsync, cancellationToken).ConfigureAwait(false);
        var record = RequestRecord(WorkgroupAuditActions.ChangeApproved, workgroup, request, request.DecisionReason);
        record = record with { Payload = [.. record.Payload, Field("supersededVersionNumber", superseding?.ToString(CultureInfo.InvariantCulture) ?? "none")] };
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<WorkgroupChangeRequestView> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var workgroup = await LoadAsync(row.WorkgroupId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        WorkgroupChangeRequest request;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor, workgroup);
            await RequireAsync(WorkgroupAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            request.Reject(actor, reason, workgroup, Now());
        }
        catch (WorkgroupRefusedException refusal)
        {
            await RefuseAsync(tx, workgroup, "reject", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(WorkgroupAuditActions.ChangeRejected, workgroup, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<WorkgroupChangeRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var workgroup = await LoadAsync(row.WorkgroupId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        WorkgroupChangeRequest request;
        try
        {
            request = Restore(row);
            await RequireAsync(WorkgroupAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            request.Withdraw(actor, reason, Now());
        }
        catch (WorkgroupRefusedException refusal)
        {
            await RefuseAsync(tx, workgroup, "withdraw", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(WorkgroupAuditActions.ChangeWithdrawn, workgroup, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<WorkgroupChangeReview?> ReviewRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await RequireAsync(WorkgroupAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var row = await db.ChangeRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var workgroup = await Current(db.Workgroups).SingleAsync(w => w.Id == row.WorkgroupId, cancellationToken).ConfigureAwait(false);
        var draft = workgroup.CurrentDraft;
        var named = draft is not null && draft.Id == row.VersionId && string.Equals(draft.ContentHash, row.ContentHash, StringComparison.Ordinal);
        return new WorkgroupChangeReview(View(Restore(row)), named ? View(draft!) : null);
    }

    private static WorkgroupChangeRequest Restore(WorkgroupChangeRequestRow row) => WorkgroupChangeRequest.Restore(
        row.Id, row.WorkgroupId, row.VersionId, row.VersionNumber, row.ContentHash, row.Maker, row.Reason, row.ProposedAtUtc,
        row.State switch
        {
            nameof(SecondPersonState.Proposed) => SecondPersonState.Proposed,
            nameof(SecondPersonState.Approved) => SecondPersonState.Approved,
            nameof(SecondPersonState.Rejected) => SecondPersonState.Rejected,
            nameof(SecondPersonState.Withdrawn) => SecondPersonState.Withdrawn,
            _ => 0,
        },
        row.Checker, row.DecisionReason, row.DecidedAtUtc);

    private static void Apply(WorkgroupChangeRequestRow row, WorkgroupChangeRequest request)
    {
        row.State = request.State.ToString();
        row.Checker = request.Checker;
        row.DecisionReason = request.DecisionReason;
        row.DecidedAtUtc = request.DecidedAtUtc;
    }

    private static WorkgroupChangeRequestView View(WorkgroupChangeRequest r) => new(
        r.Id, r.WorkgroupId, r.VersionNumber, r.ContentHash, r.Maker, r.Reason, r.ProposedAtUtc, r.State.ToString(), r.Checker, r.DecisionReason, r.DecidedAtUtc);

    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    /// <summary>Bumps the workgroup row's version without changing it, so it serialises edits and proposals.</summary>
    private void TouchWorkgroup(Workgroup workgroup) => db.Entry(workgroup).Property(w => w.DisplayName).IsModified = true;

    private async Task RequireNothingPendingAsync(Guid workgroupId, CancellationToken cancellationToken)
    {
        if (await db.ChangeRequests.AnyAsync(r => r.WorkgroupId == workgroupId && r.State == ProposedState, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.AlreadyPending);
        }
    }

    private bool IsAuthorOf(WorkgroupVersion draft, string person) =>
        string.Equals(db.Entry(draft).Property<string>(AuditColumns.CreatedBy).CurrentValue, person, StringComparison.Ordinal)
        || string.Equals(db.Entry(draft).Property<string>(AuditColumns.ModifiedBy).CurrentValue, person, StringComparison.Ordinal);

    private Task RequireAsync(string action, CancellationToken cancellationToken) =>
        RequirePersonAsync(Actor(), action, WorkgroupRefusal.NotAllowed, cancellationToken);

    private async Task RequirePersonAsync(string person, string action, WorkgroupRefusal refusal, CancellationToken cancellationToken)
    {
        if (!await permissions.CanAsync(person, action, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkgroupRefusedException(refusal);
        }
    }

    private string Actor() =>
        actorAccessor.CurrentActor is { Length: > 0 } actor ? actor : throw new WorkgroupRefusedException(WorkgroupRefusal.NotAllowed);

    private async Task<WorkgroupChangeRequestRow> LoadRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.ChangeRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false)
        ?? throw new WorkgroupNotFoundException();

    /// <summary>The workgroup with its draft and active member lists only, so loads stay bounded.</summary>
    internal static IQueryable<Workgroup> Current(IQueryable<Workgroup> workgroups) =>
        workgroups.Include(w => w.Versions.Where(v => v.State != MemberVersionState.Superseded)).ThenInclude(v => v.Members);

    private async Task<Workgroup> LoadAsync(Guid workgroupId, CancellationToken cancellationToken) =>
        await Current(db.Workgroups).SingleOrDefaultAsync(w => w.Id == workgroupId, cancellationToken).ConfigureAwait(false)
        ?? throw new WorkgroupNotFoundException();

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex) || databaseProvider.IsConcurrencyFailure(ex))
        {
            throw new WorkgroupConflictException("The workgroup or its request was changed by someone else; reload it and try again.", ex);
        }
    }

    private Task RefuseAsync(IDbContextTransaction tx, Workgroup workgroup, string step, WorkgroupRefusedException refusal, Guid? requestId, CancellationToken cancellationToken) =>
        RefuseAsync(tx, workgroup.Id, workgroup.Key, step, refusal, requestId, cancellationToken);

    /// <summary>The refusal is evidence: audited as denied with the refusal code (and the request, if any), then committed.</summary>
    private async Task RefuseAsync(IDbContextTransaction tx, Guid? workgroupId, string key, string step, WorkgroupRefusedException refusal, Guid? requestId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        List<AuditPayloadField> payload = [Field("key", key), Field("step", step), Field("refusal", (refusal.Refusal ?? WorkgroupRefusal.InvalidRecord).ToString())];
        if (requestId is { } id)
        {
            payload.Add(Field("requestId", id.ToString()));
        }

        await audit.AppendAsync(tx.GetDbTransaction(), Event(WorkgroupAuditActions.ChangeRefused, workgroupId, AuditOutcome.Denied, null, [.. payload]), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Members as a sorted, semicolon-separated list, so the audit trail alone shows who was in each list.</summary>
    private static string MembersSnapshot(WorkgroupVersion version) => string.Join(';', version.People);

    private AuditEventRecord VersionRecord(string action, Workgroup workgroup, WorkgroupVersion version) =>
        Event(action, workgroup.Id, AuditOutcome.Succeeded, null,
            Field("key", workgroup.Key),
            Field("versionNumber", version.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            Field("members", MembersSnapshot(version)),
            Field("contentHash", version.ContentHash));

    private AuditEventRecord RequestRecord(string action, Workgroup workgroup, WorkgroupChangeRequest request, string? reason)
    {
        List<AuditPayloadField> payload =
        [
            Field("key", workgroup.Key),
            Field("requestId", request.Id.ToString()),
            Field("versionNumber", request.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            Field("contentHash", request.ContentHash),
            Field("maker", request.Maker),
            Field("state", request.State.ToString()),
        ];
        if (workgroup.Versions.SingleOrDefault(v => v.Id == request.VersionId) is { } version)
        {
            payload.Add(Field("members", MembersSnapshot(version)));
        }

        return Event(action, workgroup.Id, AuditOutcome.Succeeded, reason, [.. payload]);
    }

    private AuditEventRecord Event(string action, Guid? targetId, AuditOutcome outcome, string? reason, params AuditPayloadField[] payload) => new()
    {
        Action = action,
        TargetType = WorkgroupAuditActions.TargetType,
        TargetId = targetId?.ToString(),
        Outcome = outcome,
        OccurredAtUtc = Now(),
        CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
        Reason = reason,
        Payload = payload,
    };

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    private WorkgroupView View(Workgroup workgroup) => new(
        workgroup.Id,
        workgroup.Key,
        workgroup.DisplayName,
        workgroup.CurrentActive is not null,
        workgroup.CurrentActive is { } active ? View(active) : null,
        workgroup.CurrentDraft is { } draft ? View(draft) : null,
        db.Entry(workgroup).Property<long>(AuditColumns.Version).CurrentValue);

    private WorkgroupVersionView View(WorkgroupVersion version) => new(
        version.Id, version.VersionNumber, version.State.ToString(), version.People, version.ContentHash, db.Entry(version).Property<long>(AuditColumns.Version).CurrentValue);
}

/// <summary>The read-only contract other modules use (<see cref="IWorkgroupDirectory"/>). It reads only; it needs no permission.</summary>
public sealed class WorkgroupDirectory(OrganizationDbContext db) : IWorkgroupDirectory
{
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        WorkgroupRules.IsKey(key) ? db.Workgroups.AsNoTracking().AnyAsync(w => w.Key == key, cancellationToken) : Task.FromResult(false);

    public async Task<WorkgroupMembership?> FindActiveAsync(string key, CancellationToken cancellationToken)
    {
        if (!WorkgroupRules.IsKey(key))
        {
            return null;
        }

        var workgroup = await db.Workgroups.AsNoTracking()
            .Include(w => w.Versions.Where(v => v.State == MemberVersionState.Active)).ThenInclude(v => v.Members)
            .SingleOrDefaultAsync(w => w.Key == key, cancellationToken)
            .ConfigureAwait(false);
        return workgroup?.CurrentActive is { } active ? new WorkgroupMembership(workgroup.Key, active.VersionNumber, active.ContentHash, active.People) : null;
    }
}
