using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Identity.Application.Attributes;
using IAM.Modules.Identity.Domain.Attributes;
using IAM.Modules.Identity.Infrastructure.Attributes;
using IAM.Modules.Identity.Infrastructure.Persistence;
using IAM.Persistence.Tests.Identity;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// S1-08 attribute catalog behaviour that must be identical on SQL Server and PostgreSQL (ADR-0010, ADR-0015,
/// IAM-WF-013/014/019): registration with its audit row in one transaction, exact keys, optimistic concurrency,
/// locked activation, immutable keys and immutable non-draft versions.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class AttributeCatalogParityTests(ParityDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    // ---- Registration -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EeSubgroupCodeIsRegisteredAuditedAndReadBack()
    {
        var key = NewKey("ee_subgroup_code");
        AttributeDraftView registered;
        await using (var scope = Service())
        {
            registered = await scope.Service.RegisterAsync(new RegisterAttributeRequest(key, AttributeCatalogDomainTests.EeSubgroupCodeRequest()), Ct);
        }

        registered.Key.ShouldBe(key);
        registered.VersionNumber.ShouldBe(1);
        registered.State.ShouldBe("Draft");
        registered.ConcurrencyVersion.ShouldBe(1);

        await using (var scope = Service())
        {
            var found = (await scope.Service.FindDraftAsync(key, Ct)).ShouldNotBeNull();
            found.DefinitionId.ShouldBe(registered.DefinitionId);
            found.Content.DataType.ShouldBe(AttributeDataType.Code);
            found.Content.Operators.ShouldBe([AttributeOperator.Equals, AttributeOperator.In, AttributeOperator.NotIn, AttributeOperator.IsEmpty]);
            found.ContentHash.ShouldBe(AttributeDefinitionVersion.ComputeContentHash(key, 1, found.Content));
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.Registered, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task KeysAreExactAndUnique()
    {
        var key = NewKey("company_code");
        await using (var scope = Service())
        {
            await scope.Service.RegisterAsync(new RegisterAttributeRequest(key, AttributeCatalogDomainTests.EeSubgroupCodeRequest()), Ct);
        }

        await using (var scope = Service())
        {
            (await Should.ThrowAsync<AttributeDefinitionRejectedException>(() =>
                scope.Service.RegisterAsync(new RegisterAttributeRequest(key, AttributeCatalogDomainTests.EeSubgroupCodeRequest()), Ct)))
                .Field.ShouldBe("Key");
        }

        // The unique index itself, not only the pre-check, refuses a duplicate (a concurrent registration).
        await using (var context = Identity())
        {
            context.AttributeDefinitions.Add(AttributeDefinition.Register(key, AttributeCatalogDomainTests.EeSubgroupCode()));
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        }

        await using (var scope = Service())
        {
            (await scope.Service.FindDraftAsync(key + "_x", Ct)).ShouldBeNull();
        }
    }

    /// <summary>The audit writer refuses (here: the action is not registered), so the registration must not survive.</summary>
    [Fact]
    public async Task AFailedAuditWriteLeavesNoRegistration()
    {
        var key = NewKey("cost_center");
        await using (var scope = Service(registerAuditActions: false))
        {
            await Should.ThrowAsync<AuditRecordRejectedException>(() =>
                scope.Service.RegisterAsync(new RegisterAttributeRequest(key, AttributeCatalogDomainTests.EeSubgroupCodeRequest()), Ct));
        }

        await using var check = Service();
        (await check.Service.FindDraftAsync(key, Ct)).ShouldBeNull();
    }

    // ---- Drafts and concurrency ---------------------------------------------------------------------------------

    [Fact]
    public async Task DraftsUpdateWithOptimisticConcurrency()
    {
        var registered = await RegisterAsync(NewKey("department"));
        var change = AttributeCatalogDomainTests.EeSubgroupCodeRequest() with { Operators = ["Equals", "NotEquals"], DisplayName = "Department" };

        AttributeDraftView updated;
        await using (var scope = Service())
        {
            updated = await scope.Service.UpdateDraftAsync(registered.DefinitionId, registered.ConcurrencyVersion, change, Ct);
        }

        updated.ConcurrencyVersion.ShouldBe(registered.ConcurrencyVersion + 1);
        updated.Content.Operators.ShouldBe([AttributeOperator.Equals, AttributeOperator.NotEquals]);
        updated.ContentHash.ShouldNotBe(registered.ContentHash);

        // A second editor still holding the old version is refused.
        await using (var scope = Service())
        {
            await Should.ThrowAsync<AttributeDraftConflictException>(() =>
                scope.Service.UpdateDraftAsync(registered.DefinitionId, registered.ConcurrencyVersion, change with { DisplayName = "Stale" }, Ct));
        }

        await using (var scope = Service())
        {
            var found = (await scope.Service.FindDraftAsync(registered.Key, Ct)).ShouldNotBeNull();
            found.Content.DisplayName.ShouldBe("Department");
            found.Content.Operators.ShouldBe([AttributeOperator.Equals, AttributeOperator.NotEquals]);
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.DraftUpdated, "Succeeded")).ShouldBe(1);
    }

    // ---- Activation locked (DL-007) -----------------------------------------------------------------------------

    [Fact]
    public async Task PublishingIsRefusedAndTheRefusalIsAudited()
    {
        var registered = await RegisterAsync(NewKey("division"));
        await using (var scope = Service())
        {
            await Should.ThrowAsync<AttributeActivationLockedException>(() => scope.Service.RequestPublishAsync(registered.DefinitionId, Ct));
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishRefused, "Denied")).ShouldBe(1);
        await using (var scope = Service())
        {
            (await scope.Service.FindDraftAsync(registered.Key, Ct)).ShouldNotBeNull().State.ShouldBe("Draft");
        }

        await using var reader = Identity();
        (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync(registered.Key, Ct)).ShouldBeNull("drafts are never visible to rules");
        (await new AttributeCatalogReader(reader).FindPublishedByIdAsync(registered.DefinitionId, Ct)).ShouldBeNull();
        (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync("Not A Key", Ct)).ShouldBeNull();
    }

    // ---- Immutability (database triggers) -----------------------------------------------------------------------

    [Fact]
    public async Task KeysAndIdsCannotChangeEvenThroughSql()
    {
        var registered = await RegisterAsync(NewKey("job_grade"));
        var definitions = IsSqlServer ? "[identity].[AttributeDefinition]" : "identity.attribute_definition";
        var id = $"'{registered.DefinitionId}'";
        foreach (var sql in new[]
        {
            $"UPDATE {definitions} SET {Col("Key")} = 'renamed_key' WHERE {Col("Id")} = {id}",
            $"UPDATE {definitions} SET {Col("Id")} = '{Guid.CreateVersion7()}' WHERE {Col("Id")} = {id}",
        })
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.IdentityAppConnectionString, sql, Ct)))
                .Message.ShouldMatch("(?i)(immutable|reference|foreign key)", sql);
        }

        // The version's own identity cannot change either, even while it is a draft.
        foreach (var sql in new[]
        {
            $"UPDATE {Versions} SET {Col("VersionNumber")} = 7 WHERE {Col("Id")} = '{registered.VersionId}'",
            $"UPDATE {Versions} SET {Col("Id")} = '{Guid.CreateVersion7()}' WHERE {Col("Id")} = '{registered.VersionId}'",
        })
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.IdentityAppConnectionString, sql, Ct)))
                .Message.ShouldMatch("(?i)(immutable|reference|foreign key)", sql);
        }
    }

    /// <summary>
    /// DL-007 in the database: until maker-checker exists only Draft versions may exist, so neither the Identity app
    /// login nor the schema owner can publish with SQL, by update or by inserting an already-published row.
    /// </summary>
    [Fact]
    public async Task PublishingBySqlIsRefusedByTheDatabase()
    {
        var registered = await RegisterAsync(NewKey("location_code"));
        var publish = $"UPDATE {Versions} SET {Col("State")} = 'Published' WHERE {Col("Id")} = '{registered.VersionId}'";
        foreach (var login in new[] { db.IdentityAppConnectionString, db.MigratorConnectionString })
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, publish, Ct))).Message.ShouldMatch("(?i)(check|constraint)");
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, CopyVersionSql(registered.VersionId, "Published", 2), Ct)))
                .Message.ShouldMatch("(?i)(check|constraint)");
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
                login, $"UPDATE {Versions} SET {Col("State")} = 'Active' WHERE {Col("Id")} = '{registered.VersionId}'", Ct)))
                .Message.ShouldMatch("(?i)(check|constraint)");
        }

        // A second draft of the same attribute is refused too (at most one draft).
        (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.IdentityAppConnectionString, CopyVersionSql(registered.VersionId, "Draft", 2), Ct)))
            .Message.ShouldMatch("(?i)(duplicate|unique)");

        await using var reader = Identity();
        (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync(registered.Key, Ct)).ShouldBeNull();
    }

    /// <summary>
    /// The immutability triggers for published versions are the layer that will matter once maker-checker lifts the
    /// Draft-only constraint. To prove them now, the schema owner lifts the constraint briefly, publishes one version,
    /// runs the trigger assertions, then restores the constraint (NOT VALID / WITH NOCHECK, so it is enforced again for
    /// every later change).
    /// </summary>
    [Fact]
    public async Task PublishedVersionsAndTheirOperatorsAreImmutable()
    {
        var registered = await RegisterAsync(NewKey("employee_group"));
        var id = $"'{registered.VersionId}'";
        await WithConstraintLiftedAsync(registered.VersionId, async () =>
        {
            foreach (var sql in new[]
            {
            $"UPDATE {Versions} SET {Col("DisplayName")} = 'changed' WHERE {Col("Id")} = {id}",
            $"UPDATE {Versions} SET {Col("State")} = 'Draft' WHERE {Col("Id")} = {id}",
            $"DELETE FROM {Operators} WHERE {Col("AttributeDefinitionVersionId")} = {id}",
            $"UPDATE {Operators} SET {Col("Operator")} = 'NotEquals' WHERE {Col("AttributeDefinitionVersionId")} = {id} AND {Col("Operator")} = 'Equals'",
            CopyOperatorSql(registered.VersionId, "NotEquals"),
            $"DELETE FROM {Versions} WHERE {Col("Id")} = {id}",
        })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.IdentityAppConnectionString, sql, Ct)))
                    .Message.ShouldContain("immutable", Case.Insensitive, sql);
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, sql, Ct)))
                    .Message.ShouldContain("immutable", Case.Insensitive, $"the schema owner too: {sql}");
            }
        });

        // The domain refuses too: there is no draft left to edit or publish.
        await using (var scope = Service())
        {
            await Should.ThrowAsync<AttributeDefinitionRejectedException>(() =>
                scope.Service.UpdateDraftAsync(registered.DefinitionId, 1, AttributeCatalogDomainTests.EeSubgroupCodeRequest(), Ct));
            await Should.ThrowAsync<AttributeDefinitionRejectedException>(() => scope.Service.RequestPublishAsync(registered.DefinitionId, Ct));
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishRefused, "Denied")).ShouldBe(0, "no draft means no publish attempt to record");

        // Once published, the reader exposes it to other modules, with its operators and hash.
        await using var reader = Identity();
        var published = (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync(registered.Key, Ct)).ShouldNotBeNull();
        published.VersionId.ShouldBe(registered.VersionId);
        published.DataType.ShouldBe("Code");
        published.Operators.ShouldBe(["Equals", "In", "NotIn", "IsEmpty"]);
        published.ContentHash.ShouldBe(registered.ContentHash);
    }

    /// <summary>Two editors start from the same version; the second save loses, whichever way the database reports it.</summary>
    [Fact]
    public async Task ConcurrentDraftEditsResolveToAConflict()
    {
        var registered = await RegisterAsync(NewKey("pay_scale"));
        var change = AttributeCatalogDomainTests.EeSubgroupCodeRequest() with { Operators = ["Equals", "In", "NotIn", "IsEmpty", "NotEquals"] };

        await using var editorB = Service();
        await editorB.LoadDefinitionAsync(registered.DefinitionId); // B reads version 1 before A saves

        await using (var editorA = Service())
        {
            await editorA.Service.UpdateDraftAsync(registered.DefinitionId, registered.ConcurrencyVersion, change, Ct);
        }

        // B adds the same operator from its stale copy: a stale Version and a duplicate operator row are both possible.
        await Should.ThrowAsync<AttributeDraftConflictException>(() =>
            editorB.Service.UpdateDraftAsync(registered.DefinitionId, registered.ConcurrencyVersion, change with { DisplayName = "Editor B" }, Ct));

        await using var check = Service();
        (await check.Service.FindDraftAsync(registered.Key, Ct)).ShouldNotBeNull().Content.DisplayName.ShouldBe("Employee subgroup code");
    }

    [Fact]
    public async Task AFailedAuditWriteLeavesTheDraftUnchanged()
    {
        var registered = await RegisterAsync(NewKey("org_unit"));
        await using (var scope = Service(registerAuditActions: false))
        {
            await Should.ThrowAsync<AuditRecordRejectedException>(() => scope.Service.UpdateDraftAsync(
                registered.DefinitionId,
                registered.ConcurrencyVersion,
                AttributeCatalogDomainTests.EeSubgroupCodeRequest() with { DisplayName = "Not saved" },
                Ct));
        }

        await using var check = Service();
        var found = (await check.Service.FindDraftAsync(registered.Key, Ct)).ShouldNotBeNull();
        found.Content.DisplayName.ShouldBe("Employee subgroup code");
        found.ConcurrencyVersion.ShouldBe(registered.ConcurrencyVersion);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private string Versions => IsSqlServer ? "[identity].[AttributeDefinitionVersion]" : "identity.attribute_definition_version";

    private string Operators => IsSqlServer ? "[identity].[AttributeDefinitionVersionOperator]" : "identity.attribute_definition_version_operator";

    private string Col(string pascal) => IsSqlServer ? $"[{pascal}]" : IamModelConventions.ToSnakeCase(pascal);

    private static readonly string[] VersionIdentityColumns = ["Id", "VersionNumber", "State"];

    /// <summary>INSERT … SELECT copying a version row with a new id, number and state (constant SQL; ids are GUIDs).</summary>
    private string CopyVersionSql(Guid sourceVersionId, string state, int versionNumber)
    {
        string[] copied =
        [
            "AttributeDefinitionId", "DisplayName", "DataType", "SubjectScope", "SourceSystem", "Sensitivity", "Normalization",
            "FreshnessMaxAgeMinutes", "MaxLength", "ContentHash", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version",
        ];
        var columns = string.Join(", ", VersionIdentityColumns.Concat(copied).Select(Col));
        var values = string.Join(", ", new[] { $"'{Guid.CreateVersion7()}'", versionNumber.ToString(CultureInfo.InvariantCulture), $"'{state}'" }.Concat(copied.Select(Col)));
        return $"INSERT INTO {Versions} ({columns}) SELECT {values} FROM {Versions} WHERE {Col("Id")} = '{sourceVersionId}'";
    }

    private string CopyOperatorSql(Guid versionId, string op)
    {
        string[] copied = ["AttributeDefinitionVersionId", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version"];
        var columns = string.Join(", ", copied.Append("Operator").Select(Col));
        var values = string.Join(", ", copied.Select(Col).Append($"'{op}'"));
        return $"INSERT INTO {Operators} ({columns}) SELECT {values} FROM {Operators} WHERE {Col("AttributeDefinitionVersionId")} = '{versionId}' AND {Col("Operator")} = 'Equals'";
    }

    /// <summary>
    /// Test-only: as the schema owner, lift the Draft-only constraint, publish one version, run the assertions, and restore the constraint
    /// without re-validating existing rows, so it is enforced again for every later insert and update.
    /// </summary>
    private async Task WithConstraintLiftedAsync(Guid versionId, Func<Task> assertions)
    {
        var (drop, restore) = IsSqlServer
            ? ("ALTER TABLE [identity].[AttributeDefinitionVersion] DROP CONSTRAINT [CK_AttributeDefinitionVersion_State_DraftOnly]",
               "ALTER TABLE [identity].[AttributeDefinitionVersion] WITH NOCHECK ADD CONSTRAINT [CK_AttributeDefinitionVersion_State_DraftOnly] CHECK ([State] = N'Draft')")
            : ("ALTER TABLE identity.attribute_definition_version DROP CONSTRAINT ck_attribute_definition_version_state_draft_only",
               "ALTER TABLE identity.attribute_definition_version ADD CONSTRAINT ck_attribute_definition_version_state_draft_only CHECK (state = 'Draft') NOT VALID");
        await db.ExecuteAsync(db.MigratorConnectionString, drop, Ct);
        try
        {
            await db.ExecuteAsync(db.MigratorConnectionString, $"UPDATE {Versions} SET {Col("State")} = 'Published' WHERE {Col("Id")} = '{versionId}'", Ct);
            await assertions();
        }
        finally
        {
            await db.ExecuteAsync(db.MigratorConnectionString, restore, Ct);
        }
    }

    private static string NewKey(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private IdentityDbContext Identity(string? actor = "catalog-admin") => TestSupport.CreateIdentity(
        db.Provider,
        db.IdentityAppConnectionString,
        new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));

    private ServiceScope Service(string? actor = "catalog-admin", bool registerAuditActions = true)
    {
        var context = Identity(actor);
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider),
            TimeProvider.System,
            new FixedActor(actor),
            new AuditActionCatalog(registerAuditActions ? AttributeAuditActions.All.Select(a => new AuditActionRegistration(a)) : []));
        return new ServiceScope(context, new AttributeCatalogAdministrationService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider)));
    }

    private async Task<AttributeDraftView> RegisterAsync(string key)
    {
        await using var scope = Service();
        return await scope.Service.RegisterAsync(new RegisterAttributeRequest(key, AttributeCatalogDomainTests.EeSubgroupCodeRequest()), Ct);
    }

    private async Task<long> AuditCountAsync(Guid definitionId, string action, string outcome)
    {
        var sql = IsSqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [TargetId] = '{definitionId}' AND [Action] = '{action}' AND [Outcome] = '{outcome}'"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE target_id = '{definitionId}' AND action = '{action}' AND outcome = '{outcome}'";
        return Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }

    private sealed class ServiceScope(IdentityDbContext context, AttributeCatalogAdministrationService service) : IAsyncDisposable
    {
        public AttributeCatalogAdministrationService Service { get; } = service;

        /// <summary>Loads and tracks the definition now, so a later call in this scope works from this (possibly stale) copy.</summary>
        public Task<AttributeDefinition> LoadDefinitionAsync(Guid definitionId) => context.AttributeDefinitions
            .Include(d => d.Versions).ThenInclude(v => v.Operators)
            .SingleAsync(d => d.Id == definitionId, TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
