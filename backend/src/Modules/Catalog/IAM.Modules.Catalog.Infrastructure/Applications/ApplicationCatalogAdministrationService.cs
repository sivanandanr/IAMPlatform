using System.Diagnostics;
using System.Globalization;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Catalog.Application.Applications;
using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using IAM.Modules.Organization.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using CatalogApplication = IAM.Modules.Catalog.Domain.Applications.Application;

namespace IAM.Modules.Catalog.Infrastructure.Applications;

/// <summary>Audit action codes of the application catalog (registered with the audit writer by the Catalog module).</summary>
public static class CatalogAuditActions
{
    public const string Registered = "catalog.application.registered";
    public const string Renamed = "catalog.application.renamed";
    public const string DraftUpdated = "catalog.application.draft_updated";
    public const string NewDraftStarted = "catalog.application.new_draft_started";
    public const string ChangeProposed = "catalog.application.change_proposed";
    public const string ChangeApproved = "catalog.application.change_approved";
    public const string ChangeRejected = "catalog.application.change_rejected";
    public const string ChangeWithdrawn = "catalog.application.change_withdrawn";
    public const string ChangeRefused = "catalog.application.change_refused";
    public const string RoleTypeProposed = "catalog.role_type.proposed";
    public const string RoleTypeApproved = "catalog.role_type.approved";
    public const string RoleTypeRejected = "catalog.role_type.rejected";
    public const string RoleTypeWithdrawn = "catalog.role_type.withdrawn";
    public const string RoleTypeRefused = "catalog.role_type.refused";
    public const string ApplicationTarget = "catalog.application";
    public const string RoleTypeTarget = "catalog.application_role_type";

    /// <summary>Action ids the catalog checks itself (Administration's contract, the same ids as its action catalog, DL-066).</summary>
    public const string ReadAction = AccessActions.ApplicationsRead;
    public const string ManageAction = AccessActions.ApplicationsManage;
    public const string ApproveAction = AccessActions.ApplicationsApprove;

    public static IReadOnlyList<string> All { get; } =
    [
        Registered, Renamed, DraftUpdated, NewDraftStarted, ChangeProposed, ChangeApproved, ChangeRejected, ChangeWithdrawn, ChangeRefused,
        RoleTypeProposed, RoleTypeApproved, RoleTypeRejected, RoleTypeWithdrawn, RoleTypeRefused,
    ];
}

/// <summary>
/// Application catalog administration (DL-066, DL-067). Each change and its audit event are saved in one transaction
/// (ADR-0018); every refused write, including a missing permission, is audited as denied with its code. Every call checks
/// its permission here as well as at the endpoint (ADR-0008); a refused read is audited by the endpoint's denial recorder.
/// </summary>
public sealed class ApplicationCatalogAdministrationService(
    CatalogDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions,
    IWorkgroupDirectory workgroups) : IApplicationCatalogAdministration
{
    private const string ProposedState = nameof(SecondPersonState.Proposed);

    /// <summary>The most role types the list returns; the write rate limit bounds how fast more can be proposed.</summary>
    private const int MaxListedRoleTypes = 500;

    public async Task<ApplicationView> RegisterAsync(RegisterApplicationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CatalogApplication application;
        try
        {
            await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            var settings = await ToSettingsAsync(request.Settings, cancellationToken).ConfigureAwait(false);
            application = CatalogApplication.Register(request.Key, request.DisplayName, settings);
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, CatalogAuditActions.ChangeRefused, CatalogAuditActions.ApplicationTarget, null, Field("key", SafeCode(request.Key)), "register", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (await db.Applications.AnyAsync(a => a.Key == application.Key, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRejectedException("Key", "is already registered");
        }

        db.Applications.Add(application);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            throw new ApplicationRejectedException("Key", "is already registered", ex);
        }

        var codes = await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), SettingsRecord(CatalogAuditActions.Registered, application, application.CurrentDraft!, codes), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(application, codes);
    }

    /// <summary>A name is not access (rule 11), but it is frozen while a change is pending, so a reviewer sees what they approve.</summary>
    public async Task<ApplicationView> RenameAsync(Guid applicationId, long expectedConcurrencyVersion, string displayName, CancellationToken cancellationToken)
    {
        var application = await LoadAsync(applicationId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string previous;
        bool changed;
        try
        {
            await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(applicationId, cancellationToken).ConfigureAwait(false);
            if (db.Entry(application).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
            {
                throw new ApplicationConflictException();
            }

            previous = application.DisplayName;
            changed = application.Rename(displayName);
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "rename", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(
                tx.GetDbTransaction(),
                Event(CatalogAuditActions.Renamed, CatalogAuditActions.ApplicationTarget, application.Id, AuditOutcome.Succeeded, null,
                    Field("key", application.Key), Field("previousDisplayName", previous), Field("displayName", application.DisplayName)),
                cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ViewAsync(application, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationView> UpdateDraftAsync(Guid applicationId, long expectedConcurrencyVersion, ApplicationSettingsInput settings, CancellationToken cancellationToken)
    {
        var application = await LoadAsync(applicationId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsVersion draft;
        bool changed;
        try
        {
            await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);

            // As DL-065: a draft is frozen while a request for it is pending, so what is approved is what was proposed.
            await RequireNothingPendingAsync(applicationId, cancellationToken).ConfigureAwait(false);
            draft = application.CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
            if (db.Entry(draft).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
            {
                throw new ApplicationConflictException();
            }

            changed = application.UpdateDraft(await ToSettingsAsync(settings, cancellationToken).ConfigureAwait(false));
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "update_draft", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            // Edits and proposals both bump the application row's version (TouchApplication), so whichever of two racing
            // ones commits second fails its concurrency check and a pending request always names the draft as it is.
            TouchApplication(application);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            var codes = await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), SettingsRecord(CatalogAuditActions.DraftUpdated, application, draft, codes), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ViewAsync(application, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationView> StartNewDraftAsync(Guid applicationId, ApplicationSettingsInput settings, CancellationToken cancellationToken)
    {
        var application = await LoadAsync(applicationId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsVersion draft;
        try
        {
            await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            var content = await ToSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
            var highest = await db.SettingsVersions.Where(v => v.ApplicationId == applicationId).MaxAsync(v => v.VersionNumber, cancellationToken).ConfigureAwait(false);
            draft = application.StartNewDraft(content, highest);
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "start_draft", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        var codes = await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), SettingsRecord(CatalogAuditActions.NewDraftStarted, application, draft, codes), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(application, codes);
    }

    public async Task<ApplicationView?> FindAsync(string key, CancellationToken cancellationToken)
    {
        await RequireAsync(CatalogAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        CatalogRules.ValidateKey(key, "Key");
        // Tracked, because the concurrency version the caller sends back is a shadow column EF Core only reads for tracked rows.
        var application = await Current(db.Applications)
            .SingleOrDefaultAsync(a => a.Key == key, cancellationToken)
            .ConfigureAwait(false);
        return application is null ? null : await ViewAsync(application, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationSettingsRequestView> ProposeAsync(Guid applicationId, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var application = await LoadAsync(applicationId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsRequest proposed;
        try
        {
            await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(applicationId, cancellationToken).ConfigureAwait(false);
            proposed = ApplicationSettingsRequest.Propose(Guid.CreateVersion7(), application, actor, reason, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "propose", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.SettingsRequests.Add(new ApplicationSettingsRequestRow
        {
            Id = proposed.Id,
            ApplicationId = proposed.ApplicationId,
            SettingsVersionId = proposed.SettingsVersionId,
            VersionNumber = proposed.VersionNumber,
            ContentHash = proposed.ContentHash,
            ProposedOwner = proposed.ProposedOwner,
            Maker = proposed.Maker,
            Reason = proposed.Reason,
            ProposedAtUtc = proposed.ProposedAtUtc,
            State = proposed.State.ToString(),
        });

        // An edit that read the application before this proposal then fails its concurrency check (review finding).
        TouchApplication(application);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        var codes = await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(CatalogAuditActions.ChangeProposed, application, proposed, proposed.Reason, codes), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(proposed);
    }

    /// <summary>
    /// Every check runs before anything changes; then the decided request, the superseded version and the activated draft
    /// are saved in that order (<see cref="OrderedSave"/>). Row versions catch a concurrent decision or edit; nothing is saved then.
    /// </summary>
    public async Task<ApplicationSettingsRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var application = await LoadAsync(row.ApplicationId, cancellationToken).ConfigureAwait(false);
        var superseding = application.CurrentActive?.VersionNumber;
        var codes = await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsRequest request;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor, application);
            await RequireAsync(CatalogAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);

            // A request from someone who has since lost the right to propose is not approvable.
            await RequirePersonAsync(request.Maker, CatalogAuditActions.ManageAction, ApplicationRefusal.MakerNoLongerAllowed, cancellationToken).ConfigureAwait(false);

            // Whoever wrote the draft (its creator or last editor) can't approve it either, even after a role change.
            if (application.CurrentDraft is { } draft && IsAuthorOf(draft, actor))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.MakerCannotDecide);
            }

            // The approver names the content they reviewed; it must be what was proposed.
            if (!string.Equals(reviewedContentHash, request.ContentHash, StringComparison.Ordinal))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.DraftChanged);
            }

            request.Approve(actor, reason, application, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "approve", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var activated = application.Versions.Single(v => v.ActivatedByRequestId == request.Id);
        Apply(row, request);
        await db.SaveVersionsInOrderAsync(activated, SaveAsync, cancellationToken).ConfigureAwait(false);

        var record = RequestRecord(CatalogAuditActions.ChangeApproved, application, request, request.DecisionReason, codes);
        record = record with { Payload = [.. record.Payload, Field("supersededVersionNumber", superseding?.ToString(CultureInfo.InvariantCulture) ?? "none")] };
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<ApplicationSettingsRequestView> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var application = await LoadAsync(row.ApplicationId, cancellationToken).ConfigureAwait(false);
        var codes = await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsRequest request;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor, application);
            await RequireAsync(CatalogAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            request.Reject(actor, reason, application, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "reject", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(CatalogAuditActions.ChangeRejected, application, request, request.DecisionReason, codes), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    /// <summary>Only the maker, and only while they can still manage the catalog; otherwise a checker rejects it.</summary>
    public async Task<ApplicationSettingsRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var application = await LoadAsync(row.ApplicationId, cancellationToken).ConfigureAwait(false);
        var codes = await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsRequest request;
        try
        {
            request = Restore(row);
            await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            request.Withdraw(actor, reason, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "withdraw", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(CatalogAuditActions.ChangeWithdrawn, application, request, request.DecisionReason, codes), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    /// <summary>The request with the draft it would activate: only if that draft is still the one named, with the content named.</summary>
    public async Task<ApplicationSettingsReview?> ReviewRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await RequireAsync(CatalogAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var row = await db.SettingsRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var application = await Current(db.Applications).SingleAsync(a => a.Id == row.ApplicationId, cancellationToken).ConfigureAwait(false);
        var codes = await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false);
        var draft = application.CurrentDraft;
        var named = draft is not null && draft.Id == row.SettingsVersionId && string.Equals(draft.ContentHash, row.ContentHash, StringComparison.Ordinal);
        return new ApplicationSettingsReview(View(Restore(row)), named ? View(draft!, codes) : null);
    }

    /// <summary>Built-in, pending and approved role types (rejected and withdrawn ones stay in the audit trail), at most 500.</summary>
    public async Task<IReadOnlyList<ApplicationRoleTypeView>> ListRoleTypesAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(CatalogAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var types = await db.RoleTypes.AsNoTracking()
            .Where(t => t.State == RoleTypeState.BuiltIn || t.State == RoleTypeState.Proposed || t.State == RoleTypeState.Approved)
            .OrderBy(t => t.Code)
            .Take(MaxListedRoleTypes)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return types.Select(View).ToList();
    }

    public async Task<ApplicationRoleTypeView> ProposeRoleTypeAsync(string code, string displayName, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationRoleType type;
        try
        {
            await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            type = ApplicationRoleType.Propose(code, displayName, actor, reason, Now());
            if (await db.RoleTypes.AnyAsync(t => t.Code == type.Code && (t.State == RoleTypeState.BuiltIn || t.State == RoleTypeState.Proposed || t.State == RoleTypeState.Approved), cancellationToken).ConfigureAwait(false))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.RoleTypeExists);
            }
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, CatalogAuditActions.RoleTypeRefused, CatalogAuditActions.RoleTypeTarget, null, Field("code", SafeCode(code)), "propose", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.RoleTypes.Add(type);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            // Someone proposed the same code at the same moment. The failed statement may have ended this transaction
            // (PostgreSQL), so the refusal is audited in a new one.
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var refusal = new ApplicationRefusedException(ApplicationRefusal.RoleTypeExists, ex);
            await using var retry = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await RefuseAsync(retry, CatalogAuditActions.RoleTypeRefused, CatalogAuditActions.RoleTypeTarget, null, Field("code", type.Code), "propose", refusal, null, cancellationToken).ConfigureAwait(false);
            throw refusal;
        }

        await audit.AppendAsync(tx.GetDbTransaction(), RoleTypeRecord(CatalogAuditActions.RoleTypeProposed, type, type.Reason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(type);
    }

    public Task<ApplicationRoleTypeView> ApproveRoleTypeAsync(Guid roleTypeId, string? reason, CancellationToken cancellationToken) =>
        DecideRoleTypeAsync(roleTypeId, "approve", CatalogAuditActions.RoleTypeApproved, async (type, actor) =>
        {
            type.RequireDecider(actor);
            await RequireAsync(CatalogAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            await RequirePersonAsync(type.Maker!, CatalogAuditActions.ManageAction, ApplicationRefusal.MakerNoLongerAllowed, cancellationToken).ConfigureAwait(false);
            type.Approve(actor, reason, Now());
        }, cancellationToken);

    public Task<ApplicationRoleTypeView> RejectRoleTypeAsync(Guid roleTypeId, string reason, CancellationToken cancellationToken) =>
        DecideRoleTypeAsync(roleTypeId, "reject", CatalogAuditActions.RoleTypeRejected, async (type, actor) =>
        {
            type.RequireDecider(actor);
            await RequireAsync(CatalogAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            type.Reject(actor, reason, Now());
        }, cancellationToken);

    public Task<ApplicationRoleTypeView> WithdrawRoleTypeAsync(Guid roleTypeId, string? reason, CancellationToken cancellationToken) =>
        DecideRoleTypeAsync(roleTypeId, "withdraw", CatalogAuditActions.RoleTypeWithdrawn, async (type, actor) =>
        {
            await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            type.Withdraw(actor, reason, Now());
        }, cancellationToken);

    private async Task<ApplicationRoleTypeView> DecideRoleTypeAsync(
        Guid roleTypeId, string step, string action, Func<ApplicationRoleType, string, Task> decide, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var type = await db.RoleTypes.SingleOrDefaultAsync(t => t.Id == roleTypeId, cancellationToken).ConfigureAwait(false)
            ?? throw new ApplicationNotFoundException();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            type.RequireValid();
            await decide(type, actor).ConfigureAwait(false);
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, CatalogAuditActions.RoleTypeRefused, CatalogAuditActions.RoleTypeTarget, type.Id, Field("code", type.Code), step, refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RoleTypeRecord(action, type, type.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(type);
    }

    /// <summary>Maps role type codes to ids, reading only the types named; only built-in or approved types may be used (DL-066c).</summary>
    private async Task<ApplicationSettings> ToSettingsAsync(ApplicationSettingsInput? input, CancellationToken cancellationToken)
    {
        if (input is null || input.Roles is null)
        {
            throw new ApplicationRejectedException("Settings", "are required");
        }

        if (input.Roles.Count > CatalogRules.MaxRoles)
        {
            throw new ApplicationRejectedException("Roles", $"must be a list of at most {CatalogRules.MaxRoles} roles");
        }

        if (input.Roles.Any(r => r is null))
        {
            throw new ApplicationRejectedException("Roles", "must not contain empty entries");
        }

        var requested = input.Roles.Select(r => r.RoleTypeCode).Distinct(StringComparer.Ordinal).ToList();
        var types = await db.RoleTypes.AsNoTracking()
            .Where(t => requested.Contains(t.Code) && (t.State == RoleTypeState.BuiltIn || t.State == RoleTypeState.Proposed || t.State == RoleTypeState.Approved))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var roles = new List<ApplicationRoleSetting>(input.Roles.Count);
        foreach (var role in input.Roles)
        {
            var type = types.SingleOrDefault(t => string.Equals(t.Code, role.RoleTypeCode, StringComparison.Ordinal))
                ?? throw new ApplicationRejectedException("RoleTypeCode", "is not a known role type");
            if (!type.IsUsable)
            {
                throw new ApplicationRefusedException(ApplicationRefusal.RoleTypeNotUsable);
            }

            roles.Add(new ApplicationRoleSetting(type.Id, role.Requirement, role.HolderKind, role.HolderId));
        }

        var settings = new ApplicationSettings(input.Owner, roles);
        CatalogRules.ValidateSettings(settings);

        // A group holder names an IAM workgroup (DL-068b), read through Organization's contract. Workgroups are never
        // deleted, so one that exists now still exists when the settings are approved and when levels resolve it.
        foreach (var group in roles.Where(r => r.HolderKind == RoleHolderKind.Group).Select(r => r.HolderId!).Distinct(StringComparer.Ordinal))
        {
            if (!await workgroups.ExistsAsync(group, cancellationToken).ConfigureAwait(false))
            {
                throw new ApplicationRejectedException("HolderId", "must name a registered workgroup");
            }
        }

        return settings;
    }

    private static ApplicationSettingsRequest Restore(ApplicationSettingsRequestRow row) => ApplicationSettingsRequest.Restore(
        row.Id,
        row.ApplicationId,
        row.SettingsVersionId,
        row.VersionNumber,
        row.ContentHash,
        row.ProposedOwner,
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

    private static void Apply(ApplicationSettingsRequestRow row, ApplicationSettingsRequest request)
    {
        row.State = request.State.ToString();
        row.Checker = request.Checker;
        row.DecisionReason = request.DecisionReason;
        row.DecidedAtUtc = request.DecidedAtUtc;
    }

    private static ApplicationSettingsRequestView View(ApplicationSettingsRequest r) => new(
        r.Id, r.ApplicationId, r.VersionNumber, r.ContentHash, r.ProposedOwner, r.Maker, r.Reason, r.ProposedAtUtc, r.State.ToString(), r.Checker, r.DecisionReason, r.DecidedAtUtc);

    private static ApplicationRoleTypeView View(ApplicationRoleType t) => new(
        t.Id, t.Code, t.DisplayName, t.State.ToString(), t.Maker, t.Reason, t.Checker, t.DecisionReason, t.DecidedAtUtc);

    /// <summary>The clock at storage precision, so what a call returns is exactly what a later read gives back.</summary>
    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    /// <summary>
    /// Bumps the application row's version without changing it (only DisplayName is written, with its own value): the row
    /// serialises edits and proposals. The draft row is left alone, so its CreatedBy/ModifiedBy still name its authors.
    /// </summary>
    private void TouchApplication(CatalogApplication application) => db.Entry(application).Property(a => a.DisplayName).IsModified = true;

    private async Task RequireNothingPendingAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        if (await db.SettingsRequests.AnyAsync(r => r.ApplicationId == applicationId && r.State == ProposedState, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.AlreadyPending);
        }
    }

    /// <summary>The draft's creator or last editor, from the server-side audit columns (ADR-0017).</summary>
    private bool IsAuthorOf(ApplicationSettingsVersion draft, string person) =>
        string.Equals(db.Entry(draft).Property<string>(AuditColumns.CreatedBy).CurrentValue, person, StringComparison.Ordinal)
        || string.Equals(db.Entry(draft).Property<string>(AuditColumns.ModifiedBy).CurrentValue, person, StringComparison.Ordinal);

    /// <summary>Defence in depth: the endpoint checks the same action first (ADR-0008).</summary>
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

    private async Task<ApplicationSettingsRequestRow> LoadRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.SettingsRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false)
        ?? throw new ApplicationNotFoundException();

    /// <summary>The application with its draft and active versions only: superseded ones are never needed, so loads stay bounded.</summary>
    private static IQueryable<CatalogApplication> Current(IQueryable<CatalogApplication> applications) =>
        applications.Include(a => a.Versions.Where(v => v.State != SettingsVersionState.Superseded)).ThenInclude(v => v.Roles);

    private async Task<CatalogApplication> LoadAsync(Guid applicationId, CancellationToken cancellationToken) =>
        await Current(db.Applications).SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken).ConfigureAwait(false)
        ?? throw new ApplicationNotFoundException();

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex) || databaseProvider.IsConcurrencyFailure(ex))
        {
            // Another decision on the same request, a concurrent edit or proposal, or a second pending request.
            throw new ApplicationConflictException("The application, its request or the role type was changed by someone else; reload it and try again.", ex);
        }
    }

    /// <summary>The refusal is evidence: audited as denied with the refusal code (and the request, if any), then committed.</summary>
    private Task RefuseAsync(IDbContextTransaction tx, CatalogApplication application, string step, ApplicationRefusedException refusal, Guid? requestId, CancellationToken cancellationToken) =>
        RefuseAsync(tx, CatalogAuditActions.ChangeRefused, CatalogAuditActions.ApplicationTarget, application.Id, Field("key", application.Key), step, refusal, requestId, cancellationToken);

    private async Task RefuseAsync(
        IDbContextTransaction tx, string action, string targetType, Guid? targetId, AuditPayloadField label, string step, ApplicationRefusedException refusal, Guid? requestId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        List<AuditPayloadField> payload = [label, Field("step", step), Field("refusal", (refusal.Refusal ?? ApplicationRefusal.InvalidRecord).ToString())];
        if (requestId is { } id)
        {
            payload.Add(Field("requestId", id.ToString()));
        }

        await audit.AppendAsync(tx.GetDbTransaction(), Event(action, targetType, targetId, AuditOutcome.Denied, null, [.. payload]), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A key or code exactly as given if it is a valid one; anything else is recorded as invalid, never echoed.</summary>
    private static string SafeCode(string? code)
    {
        try
        {
            CatalogRules.ValidateKey(code, "Key");
            return code!;
        }
        catch (ApplicationRejectedException)
        {
            return "(invalid)";
        }
    }

    /// <summary>
    /// Who holds which role, so the audit trail alone shows every appointment: <c>code:Requirement:Kind:Id</c> per role,
    /// sorted, separated by semicolons; an empty role is <c>code:Requirement:-</c>.
    /// </summary>
    private static string RolesSnapshot(ApplicationSettingsVersion version, Dictionary<Guid, string> codes) =>
        string.Join(';', version.Roles
            .Select(r => $"{(codes.TryGetValue(r.RoleTypeId, out var c) ? c : r.RoleTypeId.ToString())}:{r.Requirement}:{(r.HolderKind is { } k ? $"{k}:{r.HolderId}" : "-")}")
            .Order(StringComparer.Ordinal));

    private AuditEventRecord SettingsRecord(string action, CatalogApplication application, ApplicationSettingsVersion version, Dictionary<Guid, string> codes) =>
        Event(action, CatalogAuditActions.ApplicationTarget, application.Id, AuditOutcome.Succeeded, null,
            Field("key", application.Key),
            Field("versionNumber", version.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            Field("owner", version.Owner),
            Field("roles", RolesSnapshot(version, codes)),
            Field("contentHash", version.ContentHash));

    private AuditEventRecord RequestRecord(string action, CatalogApplication application, ApplicationSettingsRequest request, string? reason, Dictionary<Guid, string> codes)
    {
        List<AuditPayloadField> payload =
        [
            Field("key", application.Key),
            Field("requestId", request.Id.ToString()),
            Field("versionNumber", request.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            Field("contentHash", request.ContentHash),
            Field("proposedOwner", request.ProposedOwner),
            Field("maker", request.Maker),
            Field("state", request.State.ToString()),
        ];
        if (application.Versions.SingleOrDefault(v => v.Id == request.SettingsVersionId) is { } version)
        {
            payload.Add(Field("roles", RolesSnapshot(version, codes)));
        }

        return Event(action, CatalogAuditActions.ApplicationTarget, application.Id, AuditOutcome.Succeeded, reason, [.. payload]);
    }

    private AuditEventRecord RoleTypeRecord(string action, ApplicationRoleType type, string? reason) =>
        Event(action, CatalogAuditActions.RoleTypeTarget, type.Id, AuditOutcome.Succeeded, reason,
            Field("code", type.Code), Field("displayName", type.DisplayName), Field("maker", type.Maker ?? "none"), Field("state", type.State.ToString()));

    private AuditEventRecord Event(string action, string targetType, Guid? targetId, AuditOutcome outcome, string? reason, params AuditPayloadField[] payload) => new()
    {
        Action = action,
        TargetType = targetType,
        TargetId = targetId?.ToString(),
        Outcome = outcome,
        OccurredAtUtc = Now(),
        CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
        Reason = reason,
        Payload = payload,
    };

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    /// <summary>Codes of only the role types the application's loaded versions use.</summary>
    private async Task<Dictionary<Guid, string>> RoleTypeCodesAsync(CatalogApplication application, CancellationToken cancellationToken)
    {
        var ids = application.Versions.SelectMany(v => v.Roles).Select(r => r.RoleTypeId).Distinct().ToList();
        return await db.RoleTypes.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplicationView> ViewAsync(CatalogApplication application, CancellationToken cancellationToken) =>
        View(application, await RoleTypeCodesAsync(application, cancellationToken).ConfigureAwait(false));

    private ApplicationView View(CatalogApplication application, Dictionary<Guid, string> codes) => new(
        application.Id,
        application.Key,
        application.DisplayName,
        application.CurrentActive is not null,
        application.CurrentActive is { } active ? View(active, codes) : null,
        application.CurrentDraft is { } draft ? View(draft, codes) : null,
        db.Entry(application).Property<long>(AuditColumns.Version).CurrentValue);

    private ApplicationSettingsView View(ApplicationSettingsVersion version, Dictionary<Guid, string> codes) => new(
        version.Id,
        version.VersionNumber,
        version.State.ToString(),
        version.Owner,
        version.Roles
            .Select(r => new ApplicationRoleView(codes.TryGetValue(r.RoleTypeId, out var code) ? code : r.RoleTypeId.ToString(), r.Requirement.ToString(), r.HolderKind?.ToString(), r.HolderId))
            .OrderBy(r => r.RoleTypeCode, StringComparer.Ordinal)
            .ToList(),
        version.ContentHash,
        db.Entry(version).Property<long>(AuditColumns.Version).CurrentValue);
}
