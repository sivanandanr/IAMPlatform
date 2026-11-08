using System.Diagnostics;
using IAM.BuildingBlocks.Persistence;
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
    public const string TargetType = "identity.attribute_definition";

    public static IReadOnlyList<string> All { get; } = [Registered, DraftUpdated, PublishRefused];
}

/// <summary>
/// Catalog administration. Each change and its audit event are saved in one transaction (ADR-0018): if the audit write
/// fails, the change is rolled back. The concurrency token is the version row's <c>Version</c> column (ADR-0017).
/// </summary>
public sealed class AttributeCatalogAdministrationService(
    IdentityDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider) : IAttributeCatalogAdministration
{
    public async Task<AttributeDraftView> RegisterAsync(RegisterAttributeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        var definition = await LoadAsync(definitionId, cancellationToken).ConfigureAwait(false);
        var draft = definition.CurrentDraft ?? throw new AttributeDefinitionRejectedException("Version", "has no draft to edit");
        var versionEntry = db.Entry(draft).Property<long>(AuditColumns.Version);
        if (versionEntry.CurrentValue != expectedConcurrencyVersion)
        {
            throw new AttributeDraftConflictException();
        }

        if (!definition.UpdateDraft(content))
        {
            return View(definition, draft);
        }

        // Any content change changes ContentHash, so the version row is always updated and its Version (checked by
        // EF Core against the value loaded above, which equals the caller's) detects a concurrent edit.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task RequestPublishAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        var definition = await LoadAsync(definitionId, cancellationToken).ConfigureAwait(false);
        var draft = definition.CurrentDraft ?? throw new AttributeDefinitionRejectedException("Version", "has no draft to publish");

        // The refusal is evidence too (IAM-WF-019): record it, then refuse. Nothing else changes.
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = Record(AttributeAuditActions.PublishRefused, AuditOutcome.Denied, definition, draft) with
            {
                Reason = AttributeActivationLockedException.Reason,
            };
            await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        definition.Publish();
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
