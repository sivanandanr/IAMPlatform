using System.Diagnostics;
using System.Globalization;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Catalog.Application.Applications;
using IAM.Modules.Catalog.Application.Entitlements;
using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Domain.Entitlements;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using IAM.Modules.Organization.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Catalog.Infrastructure.Entitlements;

/// <summary>Audit action codes of the entitlement catalog (registered with the audit writer by the Catalog module).</summary>
public static class EntitlementAuditActions
{
    public const string Registered = "catalog.entitlement.registered";
    public const string Renamed = "catalog.entitlement.renamed";
    public const string DraftUpdated = "catalog.entitlement.draft_updated";
    public const string NewDraftStarted = "catalog.entitlement.new_draft_started";
    public const string OwnershipAccepted = "catalog.entitlement.ownership_accepted";
    public const string ChangeProposed = "catalog.entitlement.change_proposed";
    public const string ChangeApproved = "catalog.entitlement.change_approved";
    public const string ChangeRejected = "catalog.entitlement.change_rejected";
    public const string ChangeWithdrawn = "catalog.entitlement.change_withdrawn";
    public const string ChangeRefused = "catalog.entitlement.change_refused";
    public const string Target = "catalog.entitlement";

    public const string ReadAction = AccessActions.EntitlementsRead;
    public const string ManageAction = AccessActions.EntitlementsManage;
    public const string ApproveAction = AccessActions.EntitlementsApprove;
    public const string AcceptAction = AccessActions.EntitlementOwnershipAccept;

    public static IReadOnlyList<string> All { get; } =
    [
        Registered, Renamed, DraftUpdated, NewDraftStarted, OwnershipAccepted, ChangeProposed, ChangeApproved, ChangeRejected, ChangeWithdrawn, ChangeRefused,
    ];
}

/// <summary>
/// Entitlement catalog administration (DL-084h, DL-087). Each change and its audit event are saved in one transaction
/// (ADR-0018); every refused write, including a missing permission, is audited as denied with its code. Every call checks its
/// permission here as well as at the endpoint (ADR-0008).
/// </summary>
public sealed class EntitlementCatalogAdministrationService(
    CatalogDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions,
    IWorkgroupDirectory workgroups) : IEntitlementCatalogAdministration
{
    private const string ProposedState = nameof(SecondPersonState.Proposed);

    /// <summary>The most entitlements one list returns.</summary>
    private const int MaxListed = 500;

    public async Task<EntitlementView> RegisterAsync(Guid applicationId, string key, string displayName, EntitlementSettingsText settings, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Entitlement entitlement;
        string applicationKey;
        try
        {
            await RequireAsync(EntitlementAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            applicationKey = await db.Applications.AsNoTracking().Where(a => a.Id == applicationId).Select(a => a.Key).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ApplicationNotFoundException();
            var content = await ToSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
            entitlement = Entitlement.Register(applicationId, key, displayName, content, Actor());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, null, Field("key", SafeCode(key)), "register", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (await db.Entitlements.AnyAsync(e => e.ApplicationId == applicationId && e.Key == entitlement.Key, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRejectedException("Key", "is already registered for this application");
        }

        db.Entitlements.Add(entitlement);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            throw new ApplicationRejectedException("Key", "is already registered for this application", ex);
        }

        await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(EntitlementAuditActions.Registered, applicationKey, entitlement, entitlement.CurrentDraft!), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(applicationKey, entitlement);
    }

    /// <summary>A name is not access (rule 11), but it is frozen while a change is pending, so a reviewer sees what they approve.</summary>
    public async Task<EntitlementView> RenameAsync(Guid entitlementId, long expectedConcurrencyVersion, string displayName, CancellationToken cancellationToken)
    {
        var entitlement = await LoadAsync(entitlementId, cancellationToken).ConfigureAwait(false);
        var applicationKey = await ApplicationKeyAsync(entitlement, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string previous;
        bool changed;
        try
        {
            await RequireAsync(EntitlementAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(entitlementId, cancellationToken).ConfigureAwait(false);
            if (db.Entry(entitlement).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
            {
                throw new ApplicationConflictException();
            }

            previous = entitlement.DisplayName;
            changed = entitlement.Rename(displayName);
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, entitlement, "rename", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(
                tx.GetDbTransaction(),
                Event(EntitlementAuditActions.Renamed, entitlement.Id, AuditOutcome.Succeeded, null,
                    Field("applicationKey", applicationKey), Field("key", entitlement.Key), Field("previousDisplayName", previous), Field("displayName", entitlement.DisplayName)),
                cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return View(applicationKey, entitlement);
    }

    public async Task<EntitlementView> UpdateDraftAsync(Guid entitlementId, long expectedConcurrencyVersion, EntitlementSettingsText settings, CancellationToken cancellationToken)
    {
        var entitlement = await LoadAsync(entitlementId, cancellationToken).ConfigureAwait(false);
        var applicationKey = await ApplicationKeyAsync(entitlement, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        EntitlementVersion draft;
        bool changed;
        try
        {
            await RequireAsync(EntitlementAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);

            // A draft is frozen while a request for it is pending, so what is approved is what was proposed.
            await RequireNothingPendingAsync(entitlementId, cancellationToken).ConfigureAwait(false);
            draft = entitlement.CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
            if (db.Entry(draft).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
            {
                throw new ApplicationConflictException();
            }

            changed = entitlement.UpdateDraft(await ToSettingsAsync(settings, cancellationToken).ConfigureAwait(false), Actor());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, entitlement, "update_draft", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            // Edits, acceptances and proposals all bump the entitlement row's version, so whichever of two racing ones
            // commits second fails its concurrency check.
            Touch(entitlement);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(EntitlementAuditActions.DraftUpdated, applicationKey, entitlement, draft), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return View(applicationKey, entitlement);
    }

    public async Task<EntitlementView> StartNewDraftAsync(Guid entitlementId, EntitlementSettingsText settings, CancellationToken cancellationToken)
    {
        var entitlement = await LoadAsync(entitlementId, cancellationToken).ConfigureAwait(false);
        var applicationKey = await ApplicationKeyAsync(entitlement, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        EntitlementVersion draft;
        try
        {
            await RequireAsync(EntitlementAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            var content = await ToSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
            var highest = await db.EntitlementVersions.Where(v => v.EntitlementId == entitlementId).MaxAsync(v => v.VersionNumber, cancellationToken).ConfigureAwait(false);
            draft = entitlement.StartNewDraft(content, highest, Actor());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, entitlement, "start_draft", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Touch(entitlement);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(EntitlementAuditActions.NewDraftStarted, applicationKey, entitlement, draft), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(applicationKey, entitlement);
    }

    /// <summary>
    /// The owner accepts exactly the draft whose hash they were shown (spec v3.2 §6.3, DL-087b). For a workgroup owner any
    /// active member may accept on its behalf. Accepting changes no access and needs no permission beyond being the owner.
    /// </summary>
    public async Task<EntitlementView> AcceptOwnershipAsync(Guid entitlementId, string contentHash, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var entitlement = await LoadAsync(entitlementId, cancellationToken).ConfigureAwait(false);
        var applicationKey = await ApplicationKeyAsync(entitlement, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        EntitlementVersion draft;
        bool changed;
        try
        {
            await RequireAsync(EntitlementAuditActions.AcceptAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(entitlementId, cancellationToken).ConfigureAwait(false);
            draft = entitlement.CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
            var member = draft.OwnerKind == RoleHolderKind.Group && await IsMemberAsync(draft.OwnerId, actor, cancellationToken).ConfigureAwait(false);
            changed = entitlement.AcceptOwnership(actor, member, contentHash, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, entitlement, "accept_ownership", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            Touch(entitlement);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(
                tx.GetDbTransaction(),
                Event(EntitlementAuditActions.OwnershipAccepted, entitlement.Id, AuditOutcome.Succeeded, null,
                    Field("applicationKey", applicationKey),
                    Field("key", entitlement.Key),
                    Field("versionNumber", draft.VersionNumber.ToString(CultureInfo.InvariantCulture)),
                    Field("owner", EntitlementRules.OwnerText(draft.OwnerKind, draft.OwnerId)),
                    Field("acceptedBy", actor),
                    Field("contentHash", draft.ContentHash)),
                cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return View(applicationKey, entitlement);
    }

    public async Task<EntitlementView?> FindAsync(string applicationKey, string key, CancellationToken cancellationToken)
    {
        await RequireAsync(EntitlementAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        CatalogRules.ValidateKey(applicationKey, "ApplicationKey");
        CatalogRules.ValidateKey(key, "Key");
        var applicationId = await db.Applications.AsNoTracking().Where(a => a.Key == applicationKey).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (applicationId is null)
        {
            return null;
        }

        // Tracked, because the concurrency version the caller sends back is a shadow column EF Core only reads for tracked rows.
        var entitlement = await Current(db.Entitlements).SingleOrDefaultAsync(e => e.ApplicationId == applicationId && e.Key == key, cancellationToken).ConfigureAwait(false);
        return entitlement is null ? null : View(applicationKey, entitlement);
    }

    public async Task<IReadOnlyList<EntitlementSummary>?> ListAsync(string applicationKey, CancellationToken cancellationToken)
    {
        await RequireAsync(EntitlementAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        CatalogRules.ValidateKey(applicationKey, "ApplicationKey");
        var applicationId = await db.Applications.AsNoTracking().Where(a => a.Key == applicationKey).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (applicationId is null)
        {
            return null;
        }

        return await db.Entitlements.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId)
            .OrderBy(e => e.Key)
            .Take(MaxListed)
            .Select(e => new EntitlementSummary(
                e.Id,
                e.Key,
                e.DisplayName,
                e.Versions.Any(v => v.State == SettingsVersionState.Active),
                e.Versions.Any(v => v.State == SettingsVersionState.Draft)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EntitlementChangeRequestView> ProposeAsync(Guid entitlementId, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var entitlement = await LoadAsync(entitlementId, cancellationToken).ConfigureAwait(false);
        var applicationKey = await ApplicationKeyAsync(entitlement, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        EntitlementChangeRequest proposed;
        try
        {
            await RequireAsync(EntitlementAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(entitlementId, cancellationToken).ConfigureAwait(false);
            await RequireApplicationActiveAsync(entitlement, cancellationToken).ConfigureAwait(false);
            proposed = EntitlementChangeRequest.Propose(Guid.CreateVersion7(), entitlement, actor, reason, Now());
            await RequireTechnicalNameFreeAsync(entitlement, cancellationToken).ConfigureAwait(false);
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, entitlement, "propose", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.EntitlementRequests.Add(new EntitlementChangeRequestRow
        {
            Id = proposed.Id,
            EntitlementId = proposed.EntitlementId,
            VersionId = proposed.VersionId,
            VersionNumber = proposed.VersionNumber,
            ContentHash = proposed.ContentHash,
            OwnerKind = proposed.OwnerKind.ToString(),
            OwnerId = proposed.OwnerId,
            AcceptedBy = proposed.AcceptedBy,
            Maker = proposed.Maker,
            Reason = proposed.Reason,
            ProposedAtUtc = proposed.ProposedAtUtc,
            State = proposed.State.ToString(),
        });

        Touch(entitlement);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(EntitlementAuditActions.ChangeProposed, applicationKey, entitlement, proposed, proposed.Reason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(proposed);
    }

    /// <summary>
    /// Every check runs before anything changes; then the decided request, the superseded version and the activated draft
    /// are saved in that order (<see cref="OrderedSave"/>). Row versions catch a concurrent decision or edit.
    /// </summary>
    public async Task<EntitlementChangeRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var entitlement = await LoadAsync(row.EntitlementId, cancellationToken).ConfigureAwait(false);
        var applicationKey = await ApplicationKeyAsync(entitlement, cancellationToken).ConfigureAwait(false);
        var superseding = entitlement.CurrentActive?.VersionNumber;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        EntitlementChangeRequest request;
        try
        {
            request = Restore(row);
            var member = await IsOwnerMemberAsync(request, actor, cancellationToken).ConfigureAwait(false);
            request.RequireDecider(actor, entitlement, member);
            await RequireAsync(EntitlementAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);

            // A request from someone who has since lost the right to propose is not approvable.
            await RequirePersonAsync(request.Maker, EntitlementAuditActions.ManageAction, ApplicationRefusal.MakerNoLongerAllowed, cancellationToken).ConfigureAwait(false);

            // Whoever wrote the draft (its creator or last editor) can't approve it either.
            if (entitlement.CurrentDraft is { } draft && IsAuthorOf(draft, actor))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.MakerCannotDecide);
            }

            if (!string.Equals(reviewedContentHash, request.ContentHash, StringComparison.Ordinal))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.DraftChanged);
            }

            await RequireApplicationActiveAsync(entitlement, cancellationToken).ConfigureAwait(false);

            // Approvals in one application take turns (DL-098): the application's row is locked until this commits, so a second
            // approval waits and then sees this one's technical name. Without it, two could both find their name free.
            await db.Applications
                .Where(a => a.Id == entitlement.ApplicationId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => EF.Property<long>(a, AuditColumns.Version), a => EF.Property<long>(a, AuditColumns.Version)), cancellationToken)
                .ConfigureAwait(false);
            await RequireTechnicalNameFreeAsync(entitlement, cancellationToken).ConfigureAwait(false);
            request.Approve(actor, reason, entitlement, member, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, entitlement, "approve", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var activated = entitlement.Versions.Single(v => v.ActivatedByRequestId == request.Id);
        Apply(row, request);
        await db.SaveVersionsInOrderAsync(activated, SaveAsync, cancellationToken).ConfigureAwait(false);

        var record = RequestRecord(EntitlementAuditActions.ChangeApproved, applicationKey, entitlement, request, request.DecisionReason);
        record = record with { Payload = [.. record.Payload, Field("supersededVersionNumber", superseding?.ToString(CultureInfo.InvariantCulture) ?? "none")] };
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<EntitlementChangeRequestView> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var entitlement = await LoadAsync(row.EntitlementId, cancellationToken).ConfigureAwait(false);
        var applicationKey = await ApplicationKeyAsync(entitlement, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        EntitlementChangeRequest request;
        try
        {
            request = Restore(row);
            var member = await IsOwnerMemberAsync(request, actor, cancellationToken).ConfigureAwait(false);
            request.RequireDecider(actor, entitlement, member);
            await RequireAsync(EntitlementAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            request.Reject(actor, reason, entitlement, member, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, entitlement, "reject", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(EntitlementAuditActions.ChangeRejected, applicationKey, entitlement, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    /// <summary>Only the maker, and only while they can still manage entitlements; otherwise a checker rejects it.</summary>
    public async Task<EntitlementChangeRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var entitlement = await LoadAsync(row.EntitlementId, cancellationToken).ConfigureAwait(false);
        var applicationKey = await ApplicationKeyAsync(entitlement, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        EntitlementChangeRequest request;
        try
        {
            request = Restore(row);
            await RequireAsync(EntitlementAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            request.Withdraw(actor, reason, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, entitlement, "withdraw", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(EntitlementAuditActions.ChangeWithdrawn, applicationKey, entitlement, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<EntitlementChangeReview?> ReviewRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await RequireAsync(EntitlementAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var row = await db.EntitlementRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var entitlement = await Current(db.Entitlements).SingleAsync(e => e.Id == row.EntitlementId, cancellationToken).ConfigureAwait(false);
        var draft = entitlement.CurrentDraft;
        var named = draft is not null && draft.Id == row.VersionId && string.Equals(draft.ContentHash, row.ContentHash, StringComparison.Ordinal);
        return new EntitlementChangeReview(View(Restore(row)), named ? View(draft!) : null);
    }

    /// <summary>Parses the content and checks that both workgroups it names exist (Organization's contract).</summary>
    private async Task<EntitlementSettings> ToSettingsAsync(EntitlementSettingsText text, CancellationToken cancellationToken)
    {
        var settings = EntitlementApi.ToSettings(text);
        EntitlementRules.Validate(settings);
        if (settings.OwnerKind == RoleHolderKind.Group && !await workgroups.ExistsAsync(settings.OwnerId, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRejectedException("OwnerId", "must name a registered workgroup");
        }

        if (!await workgroups.ExistsAsync(settings.EscalationWorkgroup, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRejectedException("EscalationWorkgroup", "must name a registered workgroup");
        }

        return settings;
    }

    /// <summary>An entitlement can only be activated under an active application (DL-087).</summary>
    private async Task RequireApplicationActiveAsync(Entitlement entitlement, CancellationToken cancellationToken)
    {
        if (!await db.SettingsVersions.AnyAsync(v => v.ApplicationId == entitlement.ApplicationId && v.State == SettingsVersionState.Active, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.ApplicationNotActive);
        }
    }

    /// <summary>
    /// No other entitlement of the same application may point at the same object in the target: provisioning would then
    /// grant one thing under two names. Compared ignoring case, as directories do (DL-087).
    /// </summary>
    private async Task RequireTechnicalNameFreeAsync(Entitlement entitlement, CancellationToken cancellationToken)
    {
        var name = entitlement.CurrentDraft?.TechnicalName ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        var others = await db.EntitlementVersions.AsNoTracking()
            .Where(v => v.State == SettingsVersionState.Active && v.EntitlementId != entitlement.Id)
            .Join(db.Entitlements.Where(e => e.ApplicationId == entitlement.ApplicationId), v => v.EntitlementId, e => e.Id, (v, _) => v.TechnicalName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (others.Any(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.TechnicalNameInUse);
        }
    }

    private async Task<bool> IsMemberAsync(string workgroup, string person, CancellationToken cancellationToken) =>
        await workgroups.FindActiveAsync(workgroup, cancellationToken).ConfigureAwait(false) is { } membership
        && membership.Members.Contains(person, StringComparer.Ordinal);

    private Task<bool> IsOwnerMemberAsync(EntitlementChangeRequest request, string person, CancellationToken cancellationToken) =>
        request.OwnerKind == RoleHolderKind.Group ? IsMemberAsync(request.OwnerId, person, cancellationToken) : Task.FromResult(false);

    private static EntitlementChangeRequest Restore(EntitlementChangeRequestRow row) => EntitlementChangeRequest.Restore(
        row.Id,
        row.EntitlementId,
        row.VersionId,
        row.VersionNumber,
        row.ContentHash,
        row.OwnerKind switch
        {
            nameof(RoleHolderKind.Person) => RoleHolderKind.Person,
            nameof(RoleHolderKind.Group) => RoleHolderKind.Group,
            _ => 0,
        },
        row.OwnerId,
        row.AcceptedBy,
        row.Maker,
        row.Reason,
        row.ProposedAtUtc,
        row.State switch
        {
            nameof(SecondPersonState.Proposed) => SecondPersonState.Proposed,
            nameof(SecondPersonState.Approved) => SecondPersonState.Approved,
            nameof(SecondPersonState.Rejected) => SecondPersonState.Rejected,
            nameof(SecondPersonState.Withdrawn) => SecondPersonState.Withdrawn,
            _ => 0,
        },
        row.Checker,
        row.DecisionReason,
        row.DecidedAtUtc);

    private static void Apply(EntitlementChangeRequestRow row, EntitlementChangeRequest request)
    {
        row.State = request.State.ToString();
        row.Checker = request.Checker;
        row.DecisionReason = request.DecisionReason;
        row.DecidedAtUtc = request.DecidedAtUtc;
    }

    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    /// <summary>Bumps the entitlement row's version without changing it: the row serialises edits, acceptances and proposals.</summary>
    private void Touch(Entitlement entitlement) => db.Entry(entitlement).Property(e => e.DisplayName).IsModified = true;

    private async Task RequireNothingPendingAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        if (await db.EntitlementRequests.AnyAsync(r => r.EntitlementId == entitlementId && r.State == ProposedState, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.AlreadyPending);
        }
    }

    /// <summary>
    /// The draft's creator (server-side audit column, ADR-0017) or its last editor (<see cref="EntitlementVersion.EditedBy"/>:
    /// the row's ModifiedBy names whoever accepted ownership last, not who wrote the content).
    /// </summary>
    private bool IsAuthorOf(EntitlementVersion draft, string person) =>
        string.Equals(db.Entry(draft).Property<string>(AuditColumns.CreatedBy).CurrentValue, person, StringComparison.Ordinal)
        || string.Equals(draft.EditedBy, person, StringComparison.Ordinal);

    private Task RequireAsync(string action, CancellationToken cancellationToken) =>
        RequirePersonAsync(Actor(), action, ApplicationRefusal.NotAllowed, cancellationToken);

    private async Task RequirePersonAsync(string person, string action, ApplicationRefusal refusal, CancellationToken cancellationToken)
    {
        if (!await permissions.CanAsync(person, action, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRefusedException(refusal);
        }
    }

    private string Actor() =>
        actorAccessor.CurrentActor is { Length: > 0 } actor ? actor : throw new ApplicationRefusedException(ApplicationRefusal.NotAllowed);

    private async Task<EntitlementChangeRequestRow> LoadRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.EntitlementRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false)
        ?? throw new ApplicationNotFoundException();

    /// <summary>The entitlement with its draft and active versions only, so loads stay bounded.</summary>
    private static IQueryable<Entitlement> Current(IQueryable<Entitlement> entitlements) =>
        entitlements.Include(e => e.Versions.Where(v => v.State != SettingsVersionState.Superseded));

    private async Task<Entitlement> LoadAsync(Guid entitlementId, CancellationToken cancellationToken) =>
        await Current(db.Entitlements).SingleOrDefaultAsync(e => e.Id == entitlementId, cancellationToken).ConfigureAwait(false)
        ?? throw new ApplicationNotFoundException();

    private async Task<string> ApplicationKeyAsync(Entitlement entitlement, CancellationToken cancellationToken) =>
        await db.Applications.AsNoTracking().Where(a => a.Id == entitlement.ApplicationId).Select(a => a.Key).SingleAsync(cancellationToken).ConfigureAwait(false);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex) || databaseProvider.IsConcurrencyFailure(ex))
        {
            throw new ApplicationConflictException("The entitlement or its request was changed by someone else; reload it and try again.", ex);
        }
    }

    private Task RefuseAsync(IDbContextTransaction tx, Entitlement entitlement, string step, ApplicationRefusedException refusal, Guid? requestId, CancellationToken cancellationToken) =>
        RefuseAsync(tx, entitlement.Id, Field("key", entitlement.Key), step, refusal, requestId, cancellationToken);

    /// <summary>The refusal is evidence: audited as denied with the refusal code (and the request, if any), then committed.</summary>
    private async Task RefuseAsync(
        IDbContextTransaction tx, Guid? targetId, AuditPayloadField label, string step, ApplicationRefusedException refusal, Guid? requestId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        List<AuditPayloadField> payload = [label, Field("step", step), Field("refusal", (refusal.Refusal ?? ApplicationRefusal.InvalidRecord).ToString())];
        if (requestId is { } id)
        {
            payload.Add(Field("requestId", id.ToString()));
        }

        await audit.AppendAsync(tx.GetDbTransaction(), Event(EntitlementAuditActions.ChangeRefused, targetId, AuditOutcome.Denied, null, [.. payload]), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SafeCode(string? code) => EntitlementRules.IsKey(code) ? code! : "(invalid)";

    /// <summary>The whole content of a version, so the audit trail alone shows what each version granted and to which accounts.</summary>
    private static List<AuditPayloadField> Content(EntitlementVersion version) =>
    [
        Field("versionNumber", version.VersionNumber.ToString(CultureInfo.InvariantCulture)),
        Field("type", version.Type.ToString()),
        Field("technicalName", version.TechnicalName),
        Field("owner", EntitlementRules.OwnerText(version.OwnerKind, version.OwnerId)),
        Field("escalationWorkgroup", version.EscalationWorkgroup),
        Field("privileged", version.Privileged ? "true" : "false"),
        Field("risk", version.Risk.ToString()),
        Field("allowedAccountClasses", version.AllowedAccountClasses),
        Field("accessFor", AccessForText(version)),
        Field("editedBy", version.EditedBy),
        Field("contentHash", version.ContentHash),
    ];

    private static string AccessForText(EntitlementVersion v) => v.AccessForMode switch
    {
        AccessForMode.FixedDays => string.Create(CultureInfo.InvariantCulture, $"FixedDays:{v.AccessForDays}"),
        AccessForMode.RequesterChooses => string.Create(CultureInfo.InvariantCulture, $"RequesterChooses:{v.AccessForDefaultDays}:{v.AccessForMaxDays}"),
        _ => v.AccessForMode.ToString(),
    };

    private AuditEventRecord VersionRecord(string action, string applicationKey, Entitlement entitlement, EntitlementVersion version) =>
        Event(action, entitlement.Id, AuditOutcome.Succeeded, null,
            [Field("applicationKey", applicationKey), Field("key", entitlement.Key), Field("displayName", entitlement.DisplayName), .. Content(version)]);

    private AuditEventRecord RequestRecord(string action, string applicationKey, Entitlement entitlement, EntitlementChangeRequest request, string? reason)
    {
        List<AuditPayloadField> payload =
        [
            Field("applicationKey", applicationKey),
            Field("key", entitlement.Key),
            Field("requestId", request.Id.ToString()),
            Field("maker", request.Maker),
            Field("acceptedBy", request.AcceptedBy ?? "none"),
            Field("state", request.State.ToString()),
        ];
        if (entitlement.Versions.SingleOrDefault(v => v.Id == request.VersionId) is { } version)
        {
            payload.AddRange(Content(version));
        }
        else
        {
            payload.Add(Field("versionNumber", request.VersionNumber.ToString(CultureInfo.InvariantCulture)));
            payload.Add(Field("contentHash", request.ContentHash));
        }

        return Event(action, entitlement.Id, AuditOutcome.Succeeded, reason, [.. payload]);
    }

    private AuditEventRecord Event(string action, Guid? targetId, AuditOutcome outcome, string? reason, params AuditPayloadField[] payload) => new()
    {
        Action = action,
        TargetType = EntitlementAuditActions.Target,
        TargetId = targetId?.ToString(),
        Outcome = outcome,
        OccurredAtUtc = Now(),
        CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
        Reason = reason,
        Payload = payload,
    };

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    private static EntitlementChangeRequestView View(EntitlementChangeRequest r) => new(
        r.Id, r.EntitlementId, r.VersionNumber, r.ContentHash, r.OwnerKind.ToString(), r.OwnerId, r.AcceptedBy, r.Maker, r.Reason, r.ProposedAtUtc,
        r.State.ToString(), r.Checker, r.DecisionReason, r.DecidedAtUtc);

    private EntitlementView View(string applicationKey, Entitlement e) => new(
        e.Id,
        e.ApplicationId,
        applicationKey,
        e.Key,
        e.DisplayName,
        e.CurrentActive is not null,
        e.CurrentActive is { } active ? View(active) : null,
        e.CurrentDraft is { } draft ? View(draft) : null,
        e.DraftNeedsAcceptance,
        db.Entry(e).Property<long>(AuditColumns.Version).CurrentValue);

    private EntitlementVersionView View(EntitlementVersion v) => new(
        v.Id,
        v.VersionNumber,
        v.State.ToString(),
        v.Type.ToString(),
        v.TechnicalName,
        v.Description,
        v.OwnerKind.ToString(),
        v.OwnerId,
        v.EscalationWorkgroup,
        v.Privileged,
        v.Risk.ToString(),
        [.. v.AllowedAccountClasses.Split(',')],
        new AccessForView(v.AccessForMode.ToString(), v.AccessForDays, v.AccessForDefaultDays, v.AccessForMaxDays),
        v.ContentHash,
        v.OwnerAcceptedBy,
        v.OwnerAcceptedAtUtc,
        db.Entry(v).Property<long>(AuditColumns.Version).CurrentValue);
}
