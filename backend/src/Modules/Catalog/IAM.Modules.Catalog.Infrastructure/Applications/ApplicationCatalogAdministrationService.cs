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
    public const string ManageAction = AccessActions.ApplicationsManage;
    public const string ApproveAction = AccessActions.ApplicationsApprove;

    public static IReadOnlyList<string> All { get; } =
    [
        Registered, Renamed, DraftUpdated, NewDraftStarted, ChangeProposed, ChangeApproved, ChangeRejected, ChangeWithdrawn, ChangeRefused,
        RoleTypeProposed, RoleTypeApproved, RoleTypeRejected, RoleTypeWithdrawn, RoleTypeRefused,
    ];
}

/// <summary>
/// Application catalog administration (DL-066). Each change and its audit event are saved in one transaction (ADR-0018);
/// each refusal is audited as denied with its code. Permissions are checked here as well as at the endpoint (ADR-0008).
/// </summary>
public sealed class ApplicationCatalogAdministrationService(
    CatalogDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions) : IApplicationCatalogAdministration
{
    private const string ProposedState = nameof(SecondPersonState.Proposed);

    public async Task<ApplicationView> RegisterAsync(RegisterApplicationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
        var settings = await ToSettingsAsync(request.Settings, cancellationToken).ConfigureAwait(false);
        var application = CatalogApplication.Register(request.Key, request.DisplayName, settings);
        if (await db.Applications.AnyAsync(a => a.Key == application.Key, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationRejectedException("Key", "is already registered");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Applications.Add(application);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            throw new ApplicationRejectedException("Key", "is already registered", ex);
        }

        await audit.AppendAsync(tx.GetDbTransaction(), SettingsRecord(CatalogAuditActions.Registered, application, application.CurrentDraft!), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(application, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationView> RenameAsync(Guid applicationId, long expectedConcurrencyVersion, string displayName, CancellationToken cancellationToken)
    {
        await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
        var application = await LoadAsync(applicationId, cancellationToken).ConfigureAwait(false);
        if (db.Entry(application).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
        {
            throw new ApplicationConflictException();
        }

        var previous = application.DisplayName;
        if (!application.Rename(displayName))
        {
            return await ViewAsync(application, cancellationToken).ConfigureAwait(false);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(
            tx.GetDbTransaction(),
            Event(CatalogAuditActions.Renamed, CatalogAuditActions.ApplicationTarget, application.Id, AuditOutcome.Succeeded, null,
                Field("key", application.Key), Field("previousDisplayName", previous), Field("displayName", application.DisplayName)),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(application, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationView> UpdateDraftAsync(Guid applicationId, long expectedConcurrencyVersion, ApplicationSettingsInput settings, CancellationToken cancellationToken)
    {
        await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
        var content = await ToSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        var application = await LoadAsync(applicationId, cancellationToken).ConfigureAwait(false);
        var draft = application.CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        if (db.Entry(draft).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
        {
            throw new ApplicationConflictException();
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // As DL-065: a draft is frozen while a request for it is pending, so what is approved is what was proposed.
        if (await HasPendingRequestAsync(applicationId, cancellationToken).ConfigureAwait(false))
        {
            var refusal = new ApplicationRefusedException(ApplicationRefusal.AlreadyPending);
            await RefuseAsync(tx, application, "update_draft", refusal, cancellationToken).ConfigureAwait(false);
            throw refusal;
        }

        if (!application.UpdateDraft(content))
        {
            return await ViewAsync(application, cancellationToken).ConfigureAwait(false);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), SettingsRecord(CatalogAuditActions.DraftUpdated, application, draft), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(application, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationView> StartNewDraftAsync(Guid applicationId, ApplicationSettingsInput settings, CancellationToken cancellationToken)
    {
        await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
        var content = await ToSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        var application = await LoadAsync(applicationId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsVersion draft;
        try
        {
            draft = application.StartNewDraft(content);
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "start_draft", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), SettingsRecord(CatalogAuditActions.NewDraftStarted, application, draft), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(application, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationView?> FindAsync(string key, CancellationToken cancellationToken)
    {
        CatalogRules.ValidateKey(key, "Key");
        var application = await db.Applications
            .Include(a => a.Versions).ThenInclude(v => v.Roles)
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
            if (!await permissions.CanAsync(actor, CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.NotAllowed);
            }

            if (await HasPendingRequestAsync(applicationId, cancellationToken).ConfigureAwait(false))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.AlreadyPending);
            }

            proposed = ApplicationSettingsRequest.Propose(Guid.CreateVersion7(), application, actor, reason, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "propose", refusal, cancellationToken).ConfigureAwait(false);
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
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(CatalogAuditActions.ChangeProposed, application, proposed, proposed.Reason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(proposed);
    }

    /// <summary>
    /// Saves in a fixed order: the decided request (the database lets a version become active only for an approved request),
    /// then the superseded version, then the activated draft, so the "one active version" index never sees two. Row versions
    /// catch a concurrent decision or edit; nothing is saved then.
    /// </summary>
    public async Task<ApplicationSettingsRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var application = await LoadAsync(row.ApplicationId, cancellationToken).ConfigureAwait(false);
        var superseding = application.CurrentActive?.VersionNumber;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsRequest request;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor);
            if (!await permissions.CanAsync(actor, CatalogAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.NotAllowed);
            }

            // A request from someone who has since lost the right to propose is not approvable.
            if (!await permissions.CanAsync(request.Maker, CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.MakerNoLongerAllowed);
            }

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
            await RefuseAsync(tx, application, "approve", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        // Hold the version changes back (marking an entry unchanged restores its loaded values), then apply exactly what
        // the domain decided, one step at a time: only State (both) and ActivatedByRequestId (the draft) change.
        var activated = application.Versions.Single(v => v.ActivatedByRequestId == request.Id);
        var decided = db.ChangeTracker.Entries<ApplicationSettingsVersion>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (Entry: e, e.Entity.State, e.Entity.ActivatedByRequestId))
            .ToList();
        foreach (var (entry, _, _) in decided)
        {
            entry.State = EntityState.Unchanged;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (entry, state, activatedBy) in decided.OrderBy(d => ReferenceEquals(d.Entry.Entity, activated) ? 1 : 0))
        {
            entry.Property(v => v.State).CurrentValue = state;
            entry.Property(v => v.ActivatedByRequestId).CurrentValue = activatedBy;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = RequestRecord(CatalogAuditActions.ChangeApproved, application, request, request.DecisionReason);
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
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsRequest request;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor);
            if (!await permissions.CanAsync(actor, CatalogAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.NotAllowed);
            }

            request.Reject(actor, reason, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "reject", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(CatalogAuditActions.ChangeRejected, application, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    /// <summary>Only the maker, and only while they can still manage the catalog; otherwise a checker rejects it.</summary>
    public async Task<ApplicationSettingsRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var application = await LoadAsync(row.ApplicationId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettingsRequest request;
        try
        {
            request = Restore(row);
            if (!await permissions.CanAsync(actor, CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.NotAllowed);
            }

            request.Withdraw(actor, reason, Now());
        }
        catch (ApplicationRefusedException refusal)
        {
            await RefuseAsync(tx, application, "withdraw", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(CatalogAuditActions.ChangeWithdrawn, application, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<ApplicationSettingsReview?> ReviewRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var row = await db.SettingsRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var application = await LoadAsync(row.ApplicationId, cancellationToken).ConfigureAwait(false);
        var codes = await RoleTypeCodesAsync(cancellationToken).ConfigureAwait(false);
        var draft = application.CurrentDraft;
        return new ApplicationSettingsReview(View(Restore(row)), draft is not null && draft.Id == row.SettingsVersionId ? View(draft, codes) : null);
    }

    public async Task<IReadOnlyList<ApplicationRoleTypeView>> ListRoleTypesAsync(CancellationToken cancellationToken)
    {
        var types = await db.RoleTypes.AsNoTracking().OrderBy(t => t.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        return types.Select(View).ToList();
    }

    public async Task<ApplicationRoleTypeView> ProposeRoleTypeAsync(string code, string displayName, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        await RequireAsync(CatalogAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
        var type = ApplicationRoleType.Propose(code, displayName, actor, reason, Now());
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var taken = await db.RoleTypes.AsNoTracking().Where(t => t.Code == type.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (taken.Any(t => t.HoldsCode))
        {
            var refusal = new ApplicationRefusedException(ApplicationRefusal.RoleTypeExists);
            await RefuseRoleTypeAsync(tx, null, type.Code, "propose", refusal, cancellationToken).ConfigureAwait(false);
            throw refusal;
        }

        db.RoleTypes.Add(type);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.RoleTypeExists, ex);
        }

        await audit.AppendAsync(tx.GetDbTransaction(), RoleTypeRecord(CatalogAuditActions.RoleTypeProposed, type, type.Reason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(type);
    }

    public Task<ApplicationRoleTypeView> ApproveRoleTypeAsync(Guid roleTypeId, string? reason, CancellationToken cancellationToken) =>
        DecideRoleTypeAsync(roleTypeId, "approve", CatalogAuditActions.RoleTypeApproved, async (type, actor) =>
        {
            type.RequireDecider(actor);
            await RequireActorAsync(actor, CatalogAuditActions.ApproveAction, ApplicationRefusal.NotAllowed, cancellationToken).ConfigureAwait(false);
            await RequireActorAsync(type.Maker!, CatalogAuditActions.ManageAction, ApplicationRefusal.MakerNoLongerAllowed, cancellationToken).ConfigureAwait(false);
            type.Approve(actor, reason, Now());
        }, cancellationToken);

    public Task<ApplicationRoleTypeView> RejectRoleTypeAsync(Guid roleTypeId, string reason, CancellationToken cancellationToken) =>
        DecideRoleTypeAsync(roleTypeId, "reject", CatalogAuditActions.RoleTypeRejected, async (type, actor) =>
        {
            type.RequireDecider(actor);
            await RequireActorAsync(actor, CatalogAuditActions.ApproveAction, ApplicationRefusal.NotAllowed, cancellationToken).ConfigureAwait(false);
            type.Reject(actor, reason, Now());
        }, cancellationToken);

    public Task<ApplicationRoleTypeView> WithdrawRoleTypeAsync(Guid roleTypeId, string? reason, CancellationToken cancellationToken) =>
        DecideRoleTypeAsync(roleTypeId, "withdraw", CatalogAuditActions.RoleTypeWithdrawn, async (type, actor) =>
        {
            await RequireActorAsync(actor, CatalogAuditActions.ManageAction, ApplicationRefusal.NotAllowed, cancellationToken).ConfigureAwait(false);
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
            await RefuseRoleTypeAsync(tx, type.Id, type.Code, step, refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RoleTypeRecord(action, type, type.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(type);
    }

    /// <summary>Maps role type codes to ids; only built-in or approved types may be used (DL-066c).</summary>
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

        var types = await db.RoleTypes.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var roles = new List<ApplicationRoleSetting>(input.Roles.Count);
        foreach (var role in input.Roles)
        {
            if (role is null)
            {
                throw new ApplicationRejectedException("Roles", "must not contain empty entries");
            }

            var type = types.SingleOrDefault(t => string.Equals(t.Code, role.RoleTypeCode, StringComparison.Ordinal) && t.HoldsCode)
                ?? throw new ApplicationRejectedException("RoleTypeCode", "is not a known role type");
            if (!type.IsUsable)
            {
                throw new ApplicationRefusedException(ApplicationRefusal.RoleTypeNotUsable);
            }

            roles.Add(new ApplicationRoleSetting(type.Id, role.Requirement, role.HolderKind, role.HolderId));
        }

        var settings = new ApplicationSettings(input.Owner, roles);
        CatalogRules.ValidateSettings(settings);
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

    private Task<bool> HasPendingRequestAsync(Guid applicationId, CancellationToken cancellationToken) =>
        db.SettingsRequests.AnyAsync(r => r.ApplicationId == applicationId && r.State == ProposedState, cancellationToken);

    /// <summary>The draft's creator or last editor, from the server-side audit columns (ADR-0017).</summary>
    private bool IsAuthorOf(ApplicationSettingsVersion draft, string person) =>
        string.Equals(db.Entry(draft).Property<string>(AuditColumns.CreatedBy).CurrentValue, person, StringComparison.Ordinal)
        || string.Equals(db.Entry(draft).Property<string>(AuditColumns.ModifiedBy).CurrentValue, person, StringComparison.Ordinal);

    /// <summary>Defence in depth: the endpoint checks the same action first (ADR-0008).</summary>
    private Task RequireAsync(string action, CancellationToken cancellationToken) =>
        RequireActorAsync(Actor(), action, ApplicationRefusal.NotAllowed, cancellationToken);

    private async Task RequireActorAsync(string person, string action, ApplicationRefusal refusal, CancellationToken cancellationToken)
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

    private async Task<CatalogApplication> LoadAsync(Guid applicationId, CancellationToken cancellationToken) =>
        await db.Applications
            .Include(a => a.Versions).ThenInclude(v => v.Roles)
            .SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new ApplicationNotFoundException();

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex) || databaseProvider.IsConcurrencyFailure(ex))
        {
            // Another decision on the same request, a concurrent edit, or a second pending request.
            throw new ApplicationConflictException("The application, its request or the role type was changed by someone else; reload it and try again.", ex);
        }
    }

    /// <summary>The refusal is evidence: audited as denied with the refusal code only, then committed.</summary>
    private async Task RefuseAsync(IDbContextTransaction tx, CatalogApplication application, string step, ApplicationRefusedException refusal, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var record = Event(CatalogAuditActions.ChangeRefused, CatalogAuditActions.ApplicationTarget, application.Id, AuditOutcome.Denied, null,
            Field("key", application.Key), Field("step", step), Field("refusal", (refusal.Refusal ?? ApplicationRefusal.InvalidRecord).ToString()));
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefuseRoleTypeAsync(IDbContextTransaction tx, Guid? roleTypeId, string code, string step, ApplicationRefusedException refusal, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var record = Event(CatalogAuditActions.RoleTypeRefused, CatalogAuditActions.RoleTypeTarget, roleTypeId, AuditOutcome.Denied, null,
            Field("code", code), Field("step", step), Field("refusal", (refusal.Refusal ?? ApplicationRefusal.InvalidRecord).ToString()));
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuditEventRecord SettingsRecord(string action, CatalogApplication application, ApplicationSettingsVersion version) =>
        Event(action, CatalogAuditActions.ApplicationTarget, application.Id, AuditOutcome.Succeeded, null,
            Field("key", application.Key),
            Field("versionNumber", version.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            Field("owner", version.Owner),
            Field("roleCount", version.Roles.Count.ToString(CultureInfo.InvariantCulture)),
            Field("contentHash", version.ContentHash));

    private AuditEventRecord RequestRecord(string action, CatalogApplication application, ApplicationSettingsRequest request, string? reason) =>
        Event(action, CatalogAuditActions.ApplicationTarget, application.Id, AuditOutcome.Succeeded, reason,
            Field("key", application.Key),
            Field("requestId", request.Id.ToString()),
            Field("versionNumber", request.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            Field("contentHash", request.ContentHash),
            Field("proposedOwner", request.ProposedOwner),
            Field("maker", request.Maker),
            Field("state", request.State.ToString()));

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

    private async Task<Dictionary<Guid, string>> RoleTypeCodesAsync(CancellationToken cancellationToken) =>
        await db.RoleTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Code, cancellationToken).ConfigureAwait(false);

    private async Task<ApplicationView> ViewAsync(CatalogApplication application, CancellationToken cancellationToken)
    {
        var codes = await RoleTypeCodesAsync(cancellationToken).ConfigureAwait(false);
        return new ApplicationView(
            application.Id,
            application.Key,
            application.DisplayName,
            application.CurrentActive is not null,
            application.CurrentActive is { } active ? View(active, codes) : null,
            application.CurrentDraft is { } draft ? View(draft, codes) : null,
            db.Entry(application).Property<long>(AuditColumns.Version).CurrentValue);
    }

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
