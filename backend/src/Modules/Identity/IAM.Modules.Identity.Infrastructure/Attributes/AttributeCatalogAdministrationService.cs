using System.Diagnostics;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Identity.Application.Attributes;
using IAM.Modules.Identity.Domain.Attributes;
using IAM.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Identity.Infrastructure.Attributes;

/// <summary>Audit action codes of the attribute catalog (registered with the audit writer by the Identity module).</summary>
public static class AttributeAuditActions
{
    public const string Registered = "identity.attribute.registered";
    public const string DraftUpdated = "identity.attribute.draft_updated";
    public const string PublishRefused = "identity.attribute.publish_refused";
    public const string NewDraftStarted = "identity.attribute.new_draft_started";
    public const string PublishProposed = "identity.attribute.publish_proposed";
    public const string PublishApproved = "identity.attribute.publish_approved";
    public const string PublishRejected = "identity.attribute.publish_rejected";
    public const string PublishWithdrawn = "identity.attribute.publish_withdrawn";
    public const string TargetType = "identity.attribute_definition";

    /// <summary>Action ids the catalog checks itself (Administration's contract, the same ids as its action catalog, DL-064).</summary>
    public const string ManageAction = AccessActions.AttributesManage;
    public const string ApproveAction = AccessActions.AttributesApprove;

    public static IReadOnlyList<string> All { get; } =
        [Registered, DraftUpdated, PublishRefused, NewDraftStarted, PublishProposed, PublishApproved, PublishRejected, PublishWithdrawn];
}

/// <summary>
/// Catalog administration. Each change and its audit event are saved in one transaction (ADR-0018): if the audit write
/// fails, the change is rolled back. The concurrency token is the version row's <c>Version</c> column (ADR-0017).
/// </summary>
public sealed class AttributeCatalogAdministrationService(
    IdentityDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions) : IAttributeCatalogAdministration
{
    public async Task<AttributeDraftView> RegisterAsync(RegisterAttributeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(AttributeAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
        AttributeRules.ValidateKey(request.Key);
        var definition = AttributeDefinition.Register(request.Key!, AttributeRequestMapper.ToDraft(request.Draft));

        if (await db.AttributeDefinitions.AnyAsync(d => d.Key == definition.Key, cancellationToken).ConfigureAwait(false))
        {
            throw new AttributeDefinitionRejectedException("Key", "is already registered");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.AttributeDefinitions.Add(definition);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            // A concurrent registration of the same key lost the race to the unique index. Any other failure
            // (constraint, trigger, connection) is rethrown as it is.
            throw new AttributeDefinitionRejectedException("Key", "is already registered", ex);
        }

        var draft = definition.CurrentDraft!;
        await audit.AppendAsync(tx.GetDbTransaction(), Record(AttributeAuditActions.Registered, AuditOutcome.Succeeded, definition, draft), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(definition, draft);
    }

    public async Task<AttributeDraftView> UpdateDraftAsync(Guid definitionId, long expectedConcurrencyVersion, AttributeDraftRequest request, CancellationToken cancellationToken)
    {
        var content = AttributeRequestMapper.ToDraft(request);
        await RequireAsync(AttributeAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
        var definition = await LoadAsync(definitionId, cancellationToken).ConfigureAwait(false);
        var draft = definition.CurrentDraft ?? throw new AttributeDefinitionRejectedException("Version", "has no draft to edit");
        var versionEntry = db.Entry(draft).Property<long>(AuditColumns.Version);
        if (versionEntry.CurrentValue != expectedConcurrencyVersion)
        {
            throw new AttributeDraftConflictException();
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // DL-065: a draft is frozen while a publish request for it is pending, so what is approved is what was proposed.
        if (await HasPendingRequestAsync(definitionId, cancellationToken).ConfigureAwait(false))
        {
            var refusal = new AttributePublishRefusedException(AttributePublishRefusal.AlreadyPending);
            await RefuseAsync(tx, definition, "update_draft", refusal, cancellationToken).ConfigureAwait(false);
            throw refusal;
        }

        if (!definition.UpdateDraft(content))
        {
            return View(definition, draft);
        }

        // Any content change changes ContentHash, so the version row is always updated and its Version (checked by
        // EF Core against the value loaded above, which equals the caller's) detects a concurrent edit.
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex))
        {
            // A concurrent editor may surface as a stale Version or, if both added the same operator, as a duplicate
            // operator row; both mean "changed by someone else".
            throw new AttributeDraftConflictException("The attribute draft was changed by someone else; reload it and try again.", ex);
        }

        await audit.AppendAsync(tx.GetDbTransaction(), Record(AttributeAuditActions.DraftUpdated, AuditOutcome.Succeeded, definition, draft), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(definition, draft);
    }

    public async Task<AttributeDraftView> StartNewDraftAsync(Guid definitionId, AttributeDraftRequest request, CancellationToken cancellationToken)
    {
        var content = AttributeRequestMapper.ToDraft(request);
        await RequireAsync(AttributeAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
        var definition = await LoadAsync(definitionId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AttributeDefinitionVersion draft;
        try
        {
            draft = definition.StartNewDraft(content);
        }
        catch (AttributePublishRefusedException refusal)
        {
            await RefuseAsync(tx, definition, "start_draft", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), Record(AttributeAuditActions.NewDraftStarted, AuditOutcome.Succeeded, definition, draft), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(definition, draft);
    }

    public async Task<AttributePublishRequestView> ProposePublishAsync(Guid definitionId, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var definition = await LoadAsync(definitionId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AttributePublishRequest proposed;
        try
        {
            if (!await permissions.CanAsync(actor, AttributeAuditActions.ManageAction, cancellationToken).ConfigureAwait(false))
            {
                throw new AttributePublishRefusedException(AttributePublishRefusal.NotAllowed);
            }

            if (await HasPendingRequestAsync(definitionId, cancellationToken).ConfigureAwait(false))
            {
                throw new AttributePublishRefusedException(AttributePublishRefusal.AlreadyPending);
            }

            proposed = AttributePublishRequest.Propose(Guid.CreateVersion7(), definition, actor, reason, Now());
        }
        catch (AttributePublishRefusedException refusal)
        {
            await RefuseAsync(tx, definition, "propose", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.AttributePublishRequests.Add(new AttributePublishRequestRow
        {
            Id = proposed.Id,
            AttributeDefinitionId = proposed.AttributeDefinitionId,
            AttributeDefinitionVersionId = proposed.AttributeDefinitionVersionId,
            VersionNumber = proposed.VersionNumber,
            ContentHash = proposed.ContentHash,
            Maker = proposed.Maker,
            Reason = proposed.Reason,
            ProposedAtUtc = proposed.ProposedAtUtc,
            State = proposed.State.ToString(),
        });
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(AttributeAuditActions.PublishProposed, definition, proposed, proposed.Reason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(proposed);
    }

    /// <summary>
    /// Saves in a fixed order, one step at a time: the decided request (the database lets a version become published only
    /// for an approved request), then the retired previous version, then the published draft (so the "one published version"
    /// index never sees two). Row versions on the request and on each version row catch a concurrent decision or edit;
    /// nothing is saved then.
    /// </summary>
    public async Task<AttributePublishRequestView> ApprovePublishAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var definition = await LoadAsync(row.AttributeDefinitionId, cancellationToken).ConfigureAwait(false);
        var previous = definition.CurrentPublished;
        var retiring = previous?.VersionNumber;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AttributePublishRequest request;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor);
            if (!await permissions.CanAsync(actor, AttributeAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false))
            {
                throw new AttributePublishRefusedException(AttributePublishRefusal.NotAllowed);
            }

            // The maker must still be able to propose: a request from someone who has since lost the right is not approvable.
            if (!await permissions.CanAsync(request.Maker, AttributeAuditActions.ManageAction, cancellationToken).ConfigureAwait(false))
            {
                throw new AttributePublishRefusedException(AttributePublishRefusal.MakerNoLongerAllowed);
            }

            // DL-065: whoever wrote the draft (its creator or last editor) can't approve it either, even after a role change.
            if (definition.CurrentDraft is { } draft && IsAuthorOf(draft, actor))
            {
                throw new AttributePublishRefusedException(AttributePublishRefusal.MakerCannotDecide);
            }

            // DL-065: the approver names the content they reviewed; it must be what was proposed and what is there now.
            if (!string.Equals(reviewedContentHash, request.ContentHash, StringComparison.Ordinal))
            {
                throw new AttributePublishRefusedException(AttributePublishRefusal.DraftChanged);
            }

            request.Approve(actor, reason, definition, Now());
        }
        catch (AttributePublishRefusedException refusal)
        {
            await RefuseAsync(tx, definition, "approve", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        // The decided request first, then the retired version, then the published one (OrderedSave).
        var published = definition.Versions.Single(v => v.PublishedByRequestId == request.Id);
        Apply(row, request);
        await db.SaveVersionsInOrderAsync(published, SaveAsync, cancellationToken).ConfigureAwait(false);

        var record = RequestRecord(AttributeAuditActions.PublishApproved, definition, request, request.DecisionReason);
        record = record with
        {
            Payload =
            [
                .. record.Payload,
                new AuditPayloadField("retiredVersionNumber", retiring?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", AuditFieldClass.Plain),
            ],
        };
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<AttributePublishRequestView> RejectPublishAsync(Guid requestId, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var definition = await LoadAsync(row.AttributeDefinitionId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AttributePublishRequest request;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor);
            if (!await permissions.CanAsync(actor, AttributeAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false))
            {
                throw new AttributePublishRefusedException(AttributePublishRefusal.NotAllowed);
            }

            request.Reject(actor, reason, Now());
        }
        catch (AttributePublishRefusedException refusal)
        {
            await RefuseAsync(tx, definition, "reject", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(AttributeAuditActions.PublishRejected, definition, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    /// <summary>Only the maker, and only while they can still manage the catalog; otherwise a checker rejects it.</summary>
    public async Task<AttributePublishRequestView> WithdrawPublishAsync(Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var definition = await LoadAsync(row.AttributeDefinitionId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AttributePublishRequest request;
        try
        {
            request = Restore(row);
            if (!await permissions.CanAsync(actor, AttributeAuditActions.ManageAction, cancellationToken).ConfigureAwait(false))
            {
                throw new AttributePublishRefusedException(AttributePublishRefusal.NotAllowed);
            }

            request.Withdraw(actor, reason, Now());
        }
        catch (AttributePublishRefusedException refusal)
        {
            await RefuseAsync(tx, definition, "withdraw", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RequestRecord(AttributeAuditActions.PublishWithdrawn, definition, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public async Task<AttributePublishRequestView?> FindPublishRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var row = await db.AttributePublishRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : View(Restore(row));
    }

    public async Task<AttributePublishReview?> ReviewPublishRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var row = await db.AttributePublishRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var definition = await LoadAsync(row.AttributeDefinitionId, cancellationToken).ConfigureAwait(false);
        var draft = definition.CurrentDraft;
        return new AttributePublishReview(View(Restore(row)), draft is not null && draft.Id == row.AttributeDefinitionVersionId ? View(definition, draft) : null);
    }

    public async Task<AttributeDraftView?> FindDraftAsync(string key, CancellationToken cancellationToken)
    {
        AttributeRules.ValidateKey(key);
        var definition = await db.AttributeDefinitions
            .Include(d => d.Versions).ThenInclude(v => v.Operators)
            .SingleOrDefaultAsync(d => d.Key == key, cancellationToken)
            .ConfigureAwait(false);
        return definition?.CurrentDraft is { } draft ? View(definition, draft) : null;
    }

    private const string ProposedState = nameof(SecondPersonState.Proposed);

    private static AttributePublishRequest Restore(AttributePublishRequestRow row) => AttributePublishRequest.Restore(
        row.Id,
        row.AttributeDefinitionId,
        row.AttributeDefinitionVersionId,
        row.VersionNumber,
        row.ContentHash,
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

    private static void Apply(AttributePublishRequestRow row, AttributePublishRequest request)
    {
        row.State = request.State.ToString();
        row.Checker = request.Checker;
        row.DecisionReason = request.DecisionReason;
        row.DecidedAtUtc = request.DecidedAtUtc;
    }

    private static AttributePublishRequestView View(AttributePublishRequest r) => new(
        r.Id, r.AttributeDefinitionId, r.VersionNumber, r.ContentHash, r.Maker, r.Reason, r.ProposedAtUtc, r.State.ToString(), r.Checker, r.DecisionReason, r.DecidedAtUtc);

    /// <summary>The clock at storage precision, so what a call returns is exactly what a later read gives back.</summary>
    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    private Task<bool> HasPendingRequestAsync(Guid definitionId, CancellationToken cancellationToken) =>
        db.AttributePublishRequests.AnyAsync(r => r.AttributeDefinitionId == definitionId && r.State == ProposedState, cancellationToken);

    /// <summary>The draft's creator or last editor, from the server-side audit columns (ADR-0017).</summary>
    private bool IsAuthorOf(AttributeDefinitionVersion draft, string person) =>
        string.Equals(db.Entry(draft).Property<string>(AuditColumns.CreatedBy).CurrentValue, person, StringComparison.Ordinal)
        || string.Equals(db.Entry(draft).Property<string>(AuditColumns.ModifiedBy).CurrentValue, person, StringComparison.Ordinal);

    /// <summary>Defence in depth: the endpoint checks the same action first (ADR-0008).</summary>
    private async Task RequireAsync(string action, CancellationToken cancellationToken)
    {
        if (!await permissions.CanAsync(Actor(), action, cancellationToken).ConfigureAwait(false))
        {
            throw new AttributePublishRefusedException(AttributePublishRefusal.NotAllowed);
        }
    }

    private string Actor() =>
        actorAccessor.CurrentActor is { Length: > 0 } actor ? actor : throw new AttributePublishRefusedException(AttributePublishRefusal.NotAllowed);

    private async Task<AttributePublishRequestRow> LoadRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.AttributePublishRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false)
        ?? throw new AttributePublishRequestNotFoundException();

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex) || databaseProvider.IsConcurrencyFailure(ex))
        {
            // Another decision on the same request, a concurrent edit of the draft, or a second pending request.
            throw new AttributePublishConflictException("The attribute or its publish request was changed by someone else; reload it and try again.", ex);
        }
    }

    /// <summary>The refusal is evidence (IAM-WF-019): audited as denied with the refusal code only, then committed.</summary>
    private async Task RefuseAsync(IDbContextTransaction tx, AttributeDefinition definition, string step, AttributePublishRefusedException refusal, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var record = new AuditEventRecord
        {
            Action = AttributeAuditActions.PublishRefused,
            TargetType = AttributeAuditActions.TargetType,
            TargetId = definition.Id.ToString(),
            Outcome = AuditOutcome.Denied,
            OccurredAtUtc = Now(),
            CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
            Payload =
            [
                new AuditPayloadField("key", definition.Key, AuditFieldClass.Plain),
                new AuditPayloadField("step", step, AuditFieldClass.Plain),
                new AuditPayloadField("refusal", (refusal.Refusal ?? AttributePublishRefusal.InvalidRecord).ToString(), AuditFieldClass.Plain),
            ],
        };
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuditEventRecord RequestRecord(string action, AttributeDefinition definition, AttributePublishRequest request, string? reason) => new()
    {
        Action = action,
        TargetType = AttributeAuditActions.TargetType,
        TargetId = definition.Id.ToString(),
        Outcome = AuditOutcome.Succeeded,
        OccurredAtUtc = Now(),
        CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
        Reason = reason,
        Payload =
        [
            new AuditPayloadField("key", definition.Key, AuditFieldClass.Plain),
            new AuditPayloadField("requestId", request.Id.ToString(), AuditFieldClass.Plain),
            new AuditPayloadField("versionNumber", request.VersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditFieldClass.Plain),
            new AuditPayloadField("contentHash", request.ContentHash, AuditFieldClass.Plain),
            new AuditPayloadField("maker", request.Maker, AuditFieldClass.Plain),
            new AuditPayloadField("state", request.State.ToString(), AuditFieldClass.Plain),
        ],
    };

    private async Task<AttributeDefinition> LoadAsync(Guid definitionId, CancellationToken cancellationToken) =>
        await db.AttributeDefinitions
            .Include(d => d.Versions).ThenInclude(v => v.Operators)
            .SingleOrDefaultAsync(d => d.Id == definitionId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new AttributeDefinitionNotFoundException();

    private AttributeDraftView View(AttributeDefinition definition, AttributeDefinitionVersion version) => new(
        definition.Id,
        definition.Key,
        version.Id,
        version.VersionNumber,
        version.State.ToString(),
        version.ToDraft(),
        version.ContentHash,
        db.Entry(version).Property<long>(AuditColumns.Version).CurrentValue);

    private AuditEventRecord Record(string action, AuditOutcome outcome, AttributeDefinition definition, AttributeDefinitionVersion version) => new()
    {
        Action = action,
        TargetType = AttributeAuditActions.TargetType,
        TargetId = definition.Id.ToString(),
        Outcome = outcome,
        OccurredAtUtc = clock.GetUtcNow(),
        CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
        Payload =
        [
            new AuditPayloadField("key", definition.Key, AuditFieldClass.Plain),
            new AuditPayloadField("versionNumber", version.VersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditFieldClass.Plain),
            new AuditPayloadField("dataType", version.DataType.ToString(), AuditFieldClass.Plain),
            new AuditPayloadField("sensitivity", version.Sensitivity.ToString(), AuditFieldClass.Plain),
            new AuditPayloadField("contentHash", version.ContentHash, AuditFieldClass.Plain),
        ],
    };
}
