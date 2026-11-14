using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
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

    // ---- Publishing with a second person (DL-007, DL-064) ------------------------------------------------------------

    [Fact]
    public async Task PublishingNeedsASecondPersonAndIsAudited()
    {
        var registered = await RegisterAsync(NewKey("division"));
        AttributePublishRequestView proposed;
        await using (var scope = Service())
        {
            proposed = await scope.Service.ProposePublishAsync(registered.DefinitionId, "Needed for the approval matrix", Ct);
        }

        proposed.State.ShouldBe("Proposed");
        proposed.Maker.ShouldBe(Author);
        proposed.ContentHash.ShouldBe(registered.ContentHash);

        // Not visible to rules yet, and the maker can't decide it.
        await using (var reader = Identity())
        {
            (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync(registered.Key, Ct)).ShouldBeNull("drafts are never visible to rules");
        }

        await using (var scope = Service())
        {
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ApprovePublishAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.MakerCannotDecide);
        }

        await using (var scope = Service(Checker))
        {
            var approved = await scope.Service.ApprovePublishAsync(proposed.Id, proposed.ContentHash, "Checked with the business owner", Ct);
            approved.State.ShouldBe("Approved");
            approved.Checker.ShouldBe(Checker);
        }

        await using (var reader = Identity())
        {
            var published = (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync(registered.Key, Ct)).ShouldNotBeNull();
            published.VersionId.ShouldBe(registered.VersionId);
            published.ContentHash.ShouldBe(registered.ContentHash);
            (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync("Not A Key", Ct)).ShouldBeNull();
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishProposed, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishRefused, "Denied")).ShouldBe(1);
        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishApproved, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task ANewVersionReplacesThePublishedOneAndRetiresIt()
    {
        var registered = await RegisterAsync(NewKey("cost_center"));
        await PublishAsync(registered.DefinitionId);
        AttributeDraftView second;
        await using (var scope = Service())
        {
            second = await scope.Service.StartNewDraftAsync(registered.DefinitionId, AttributeCatalogDomainTests.EeSubgroupCodeRequest() with { DisplayName = "Cost centre (v2)" }, Ct);
            (await Should.ThrowAsync<AttributePublishRefusedException>(() =>
                scope.Service.StartNewDraftAsync(registered.DefinitionId, AttributeCatalogDomainTests.EeSubgroupCodeRequest(), Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.DraftExists);
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishRefused, "Denied")).ShouldBe(1, "a refused new draft is audited");
        second.VersionNumber.ShouldBe(2);
        await PublishAsync(registered.DefinitionId);

        await using var reader = Identity();
        var published = (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync(registered.Key, Ct)).ShouldNotBeNull();
        published.VersionId.ShouldBe(second.VersionId);
        var states = await reader.AttributeDefinitionVersions.Where(v => v.AttributeDefinitionId == registered.DefinitionId)
            .OrderBy(v => v.VersionNumber).Select(v => v.State).ToListAsync(Ct);
        states.ShouldBe([AttributeVersionState.Retired, AttributeVersionState.Published]);
    }

    /// <summary>DL-065: while a publish request is pending, the draft can't change, so what is approved is what was proposed.</summary>
    [Fact]
    public async Task ADraftIsFrozenWhileAPublishIsPending()
    {
        var registered = await RegisterAsync(NewKey("grade_band"));
        await using (var scope = Service())
        {
            await scope.Service.ProposePublishAsync(registered.DefinitionId, "Ready", Ct);
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.UpdateDraftAsync(
                registered.DefinitionId, registered.ConcurrencyVersion, AttributeCatalogDomainTests.EeSubgroupCodeRequest() with { DisplayName = "Edited later" }, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.AlreadyPending);
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishRefused, "Denied")).ShouldBe(1);
        await using var check = Service();
        (await check.Service.FindDraftAsync(registered.Key, Ct)).ShouldNotBeNull().Content.DisplayName.ShouldBe("Employee subgroup code");
    }

    /// <summary>DL-065: the approver names the content they reviewed; anything but the proposed content is refused.</summary>
    [Fact]
    public async Task AnApproverMustNameTheContentTheyReviewed()
    {
        var registered = await RegisterAsync(NewKey("pay_band"));
        AttributePublishRequestView proposed;
        await using (var scope = Service())
        {
            proposed = await scope.Service.ProposePublishAsync(registered.DefinitionId, "Ready", Ct);
        }

        await using (var scope = Service(Checker))
        {
            var review = (await scope.Service.ReviewPublishRequestAsync(proposed.Id, Ct)).ShouldNotBeNull();
            review.Draft.ShouldNotBeNull().ContentHash.ShouldBe(proposed.ContentHash);
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ApprovePublishAsync(proposed.Id, new string('0', 64), null, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.DraftChanged);
            (await scope.Service.FindPublishRequestAsync(proposed.Id, Ct)).ShouldNotBeNull().State.ShouldBe("Proposed");
            (await scope.Service.ApprovePublishAsync(proposed.Id, review.Draft.ContentHash, null, Ct)).State.ShouldBe("Approved");
            (await scope.Service.ReviewPublishRequestAsync(proposed.Id, Ct)).ShouldNotBeNull().Draft.ShouldBeNull("nothing is a draft any more");
        }
    }

    /// <summary>DL-065: whoever created or last edited the draft can't approve it, even holding the approve permission.</summary>
    [Fact]
    public async Task TheDraftsAuthorCannotApproveItEvenAfterARoleChange()
    {
        var both = new FakePermissions(("role-swapper", AttributeAuditActions.ManageAction), ("role-swapper", AttributeAuditActions.ApproveAction), (Author, AttributeAuditActions.ManageAction));
        AttributeDraftView registered;
        await using (var scope = Service("role-swapper", permissions: both))
        {
            registered = await scope.Service.RegisterAsync(new RegisterAttributeRequest(NewKey("swap_code"), AttributeCatalogDomainTests.EeSubgroupCodeRequest()), Ct);
        }

        AttributePublishRequestView proposed;
        await using (var scope = Service(Author, permissions: both))
        {
            proposed = await scope.Service.ProposePublishAsync(registered.DefinitionId, "Proposed by someone else", Ct);
        }

        await using (var scope = Service("role-swapper", permissions: both))
        {
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ApprovePublishAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.MakerCannotDecide);
        }
    }

    [Fact]
    public async Task OnlyAllowedPeopleDecideAndOnlyOnce()
    {
        var registered = await RegisterAsync(NewKey("band_code"));
        AttributePublishRequestView proposed;
        await using (var scope = Service())
        {
            proposed = await scope.Service.ProposePublishAsync(registered.DefinitionId, "Ready", Ct);
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ProposePublishAsync(registered.DefinitionId, "Again", Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.AlreadyPending);
        }

        await using (var scope = Service("outsider"))
        {
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ApprovePublishAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NotAllowed);
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ProposePublishAsync(registered.DefinitionId, "Me too", Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NotAllowed);
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.WithdrawPublishAsync(proposed.Id, null, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NotAllowed);

            // The service checks the manage permission on every catalog write, not only at the endpoint.
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.RegisterAsync(
                new RegisterAttributeRequest(NewKey("outsider_code"), AttributeCatalogDomainTests.EeSubgroupCodeRequest()), Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NotAllowed);
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.UpdateDraftAsync(
                registered.DefinitionId, registered.ConcurrencyVersion, AttributeCatalogDomainTests.EeSubgroupCodeRequest(), Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NotAllowed);
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.StartNewDraftAsync(
                registered.DefinitionId, AttributeCatalogDomainTests.EeSubgroupCodeRequest(), Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NotAllowed);
        }

        // Someone who may manage the catalog, but didn't propose it, can't withdraw it either.
        await using (var scope = Service("temporary-author"))
        {
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.WithdrawPublishAsync(proposed.Id, null, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NotMaker);
        }

        await using (var scope = Service(Checker))
        {
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.RejectPublishAsync(proposed.Id, " ", Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.InvalidReason);
            await scope.Service.ApprovePublishAsync(proposed.Id, proposed.ContentHash, null, Ct);
        }

        await using (var scope = Service(SecondChecker))
        {
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ApprovePublishAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NotProposed);
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishApproved, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task AMakerWhoLostThePermissionCannotHaveTheRequestApproved()
    {
        var registered = await RegisterAsync(NewKey("site_code"));
        AttributePublishRequestView proposed;
        await using (var scope = Service("temporary-author"))
        {
            proposed = await scope.Service.ProposePublishAsync(registered.DefinitionId, "Ready", Ct);
        }

        await using (var scope = Service(Checker, permissions: new FakePermissions((Checker, AttributeAuditActions.ApproveAction))))
        {
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ApprovePublishAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.MakerNoLongerAllowed);
            (await scope.Service.RejectPublishAsync(proposed.Id, "Maker left", Ct)).State.ShouldBe("Rejected");
        }
    }

    [Fact]
    public async Task DecidedRequestsAreImmutableInTheDatabase()
    {
        var registered = await RegisterAsync(NewKey("unit_code"));
        var request = await PublishAsync(registered.DefinitionId);
        var requests = IsSqlServer ? "[identity].[AttributePublishRequest]" : "identity.attribute_publish_request";
        foreach (var login in new[] { db.IdentityAppConnectionString, db.MigratorConnectionString })
        {
            foreach (var sql in new[]
            {
                $"UPDATE {requests} SET {Col("DecisionReason")} = 'edited' WHERE {Col("Id")} = '{request}'",
                $"UPDATE {requests} SET {Col("Maker")} = 'someone' WHERE {Col("Id")} = '{request}'",
                $"DELETE FROM {requests} WHERE {Col("Id")} = '{request}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct))).Message.ShouldMatch("(?i)(immutable|reference|foreign key)", sql);
            }
        }

        // A request nothing references (rejected) can't be changed or deleted either: here the trigger itself refuses.
        var rejected = await RegisterAsync(NewKey("rejected_code"));
        Guid rejectedId;
        await using (var scope = Service())
        {
            rejectedId = (await scope.Service.ProposePublishAsync(rejected.DefinitionId, "Ready", Ct)).Id;
        }

        await using (var scope = Service(Checker))
        {
            await scope.Service.RejectPublishAsync(rejectedId, "Not needed", Ct);
        }

        foreach (var login in new[] { db.IdentityAppConnectionString, db.MigratorConnectionString })
        {
            foreach (var sql in new[]
            {
                $"DELETE FROM {requests} WHERE {Col("Id")} = '{rejectedId}'",
                $"UPDATE {requests} SET {Col("State")} = 'Proposed', {Col("Checker")} = NULL, {Col("DecisionReason")} = NULL, {Col("DecidedAtUtc")} = NULL WHERE {Col("Id")} = '{rejectedId}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct))).Message.ShouldContain("immutable", Case.Insensitive, sql);
            }
        }

        // A self-approved request can't even be written.
        var insert = $"INSERT INTO {requests} ({string.Join(", ", RequestColumns.Select(Col))}) "
            + $"VALUES ('{Guid.CreateVersion7()}', '{registered.DefinitionId}', '{registered.VersionId}', 1, '{new string('a', 64)}', 'self', 'r', '2026-11-13T00:00:00+00:00', 'Approved', 'self', '2026-11-13T00:00:00+00:00', '2026-11-13T00:00:00+00:00', 't', '2026-11-13T00:00:00+00:00', 't', 1)";
        (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.IdentityAppConnectionString, insert, Ct))).Message.ShouldMatch("(?i)(check|constraint)");
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
    /// DL-007 / DL-064 in the database: a version becomes published only by an approved publish request that names it, so
    /// neither the Identity app login nor the schema owner can publish with SQL, by update, with a pending request, or by
    /// inserting an already-published row; and unknown states are refused.
    /// </summary>
    [Fact]
    public async Task PublishingBySqlIsRefusedByTheDatabase()
    {
        var registered = await RegisterAsync(NewKey("location_code"));
        AttributePublishRequestView pending;
        await using (var scope = Service())
        {
            pending = await scope.Service.ProposePublishAsync(registered.DefinitionId, "Pending", Ct);
        }

        var publish = $"UPDATE {Versions} SET {Col("State")} = 'Published' WHERE {Col("Id")} = '{registered.VersionId}'";
        var publishWithPending = $"UPDATE {Versions} SET {Col("State")} = 'Published', {Col("PublishedByRequestId")} = '{pending.Id}' WHERE {Col("Id")} = '{registered.VersionId}'";
        foreach (var login in new[] { db.IdentityAppConnectionString, db.MigratorConnectionString })
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, publish, Ct))).Message.ShouldMatch("(?i)(check|constraint|approved publish request)");
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, publishWithPending, Ct))).Message.ShouldContain("approved publish request", Case.Insensitive);
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, CopyVersionSql(registered.VersionId, "Published", 2), Ct)))
                .Message.ShouldMatch("(?i)(check|constraint|approved publish request)");
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
                login, $"UPDATE {Versions} SET {Col("State")} = 'Active' WHERE {Col("Id")} = '{registered.VersionId}'", Ct)))
                .Message.ShouldMatch("(?i)(check|constraint|approved publish request)");
        }

        // DL-065c: an approved request publishes only the attribute, version number and content it names. Forged approved
        // requests (the app login can write them, R-033) that differ in content hash or version number can't publish the draft.
        var requests = IsSqlServer ? "[identity].[AttributePublishRequest]" : "identity.attribute_publish_request";
        foreach (var (hash, number) in new[] { (new string('a', 64), 1), (registered.ContentHash, 2) })
        {
            var forged = Guid.CreateVersion7();
            await db.ExecuteAsync(
                db.IdentityAppConnectionString,
                $"INSERT INTO {requests} ({string.Join(", ", RequestColumns.Select(Col))}) "
                + $"VALUES ('{forged}', '{registered.DefinitionId}', '{registered.VersionId}', {number}, '{hash}', 'maker', 'r', '2026-11-13T00:00:00+00:00', 'Approved', 'checker', '2026-11-13T00:00:00+00:00', '2026-11-13T00:00:00+00:00', 't', '2026-11-13T00:00:00+00:00', 't', 1)",
                Ct);
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
                db.IdentityAppConnectionString,
                $"UPDATE {Versions} SET {Col("State")} = 'Published', {Col("PublishedByRequestId")} = '{forged}' WHERE {Col("Id")} = '{registered.VersionId}'",
                Ct)))
                .Message.ShouldContain("approved publish request", Case.Insensitive, $"hash {hash[..8]}, version {number}");
        }

        // A second draft of the same attribute is refused too (at most one draft).
        (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.IdentityAppConnectionString, CopyVersionSql(registered.VersionId, "Draft", 2), Ct)))
            .Message.ShouldMatch("(?i)(duplicate|unique)");

        await using var reader = Identity();
        (await new AttributeCatalogReader(reader).FindPublishedByKeyAsync(registered.Key, Ct)).ShouldBeNull();
    }

    /// <summary>Once published (through an approved request), a version and its operators never change, except retirement.</summary>
    [Fact]
    public async Task PublishedVersionsAndTheirOperatorsAreImmutable()
    {
        var registered = await RegisterAsync(NewKey("employee_group"));
        await PublishAsync(registered.DefinitionId);
        var id = $"'{registered.VersionId}'";
        foreach (var sql in new[]
        {
            $"UPDATE {Versions} SET {Col("DisplayName")} = 'changed' WHERE {Col("Id")} = {id}",
            $"UPDATE {Versions} SET {Col("DisplayName")} = UPPER({Col("DisplayName")}) WHERE {Col("Id")} = {id}",
            $"UPDATE {Versions} SET {Col("State")} = 'Draft', {Col("PublishedByRequestId")} = NULL WHERE {Col("Id")} = {id}",
            $"DELETE FROM {Operators} WHERE {Col("AttributeDefinitionVersionId")} = {id}",
            $"UPDATE {Operators} SET {Col("Operator")} = 'NotEquals' WHERE {Col("AttributeDefinitionVersionId")} = {id} AND {Col("Operator")} = 'Equals'",
            CopyOperatorSql(registered.VersionId, "NotEquals"),
            $"DELETE FROM {Versions} WHERE {Col("Id")} = {id}",
        })
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.IdentityAppConnectionString, sql, Ct)))
                .Message.ShouldMatch("(?i)(immutable|check|constraint)", sql);
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, sql, Ct)))
                .Message.ShouldMatch("(?i)(immutable|check|constraint)", $"the schema owner too: {sql}");
        }

        // The domain refuses too: there is no draft left to edit or publish, and that refusal is audited.
        await using (var scope = Service())
        {
            await Should.ThrowAsync<AttributeDefinitionRejectedException>(() =>
                scope.Service.UpdateDraftAsync(registered.DefinitionId, 1, AttributeCatalogDomainTests.EeSubgroupCodeRequest(), Ct));
            (await Should.ThrowAsync<AttributePublishRefusedException>(() => scope.Service.ProposePublishAsync(registered.DefinitionId, "Again", Ct)))
                .Refusal.ShouldBe(AttributePublishRefusal.NoDraft);
        }

        (await AuditCountAsync(registered.DefinitionId, AttributeAuditActions.PublishRefused, "Denied")).ShouldBe(1);

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

    private static string NewKey(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private IdentityDbContext Identity(string? actor = "catalog-admin") => TestSupport.CreateIdentity(
        db.Provider,
        db.IdentityAppConnectionString,
        new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));

    private static readonly string[] RequestColumns =
    [
        "Id", "AttributeDefinitionId", "AttributeDefinitionVersionId", "VersionNumber", "ContentHash", "Maker", "Reason", "ProposedAtUtc",
        "State", "Checker", "DecidedAtUtc", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version",
    ];

    private const string Author = "catalog-admin";
    private const string Checker = "catalog-checker";
    private const string SecondChecker = "catalog-checker-2";

    /// <summary>Who may manage and approve in these tests (the real answer comes from the access model, DL-064).</summary>
    private static readonly FakePermissions DefaultPermissions = new(
        (Author, AttributeAuditActions.ManageAction),
        ("temporary-author", AttributeAuditActions.ManageAction),
        (Checker, AttributeAuditActions.ApproveAction),
        (SecondChecker, AttributeAuditActions.ApproveAction));

    private ServiceScope Service(string? actor = Author, bool registerAuditActions = true, FakePermissions? permissions = null)
    {
        var context = Identity(actor);
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider),
            TimeProvider.System,
            new FixedActor(actor),
            new AuditActionCatalog(registerAuditActions ? AttributeAuditActions.All.Select(a => new AuditActionRegistration(a)) : []));
        return new ServiceScope(context, new AttributeCatalogAdministrationService(
            context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions ?? DefaultPermissions));
    }

    /// <summary>Proposes publishing the current draft as the author and approves it as the checker; returns the request id.</summary>
    private async Task<Guid> PublishAsync(Guid definitionId)
    {
        AttributePublishRequestView proposed;
        await using (var scope = Service())
        {
            proposed = await scope.Service.ProposePublishAsync(definitionId, "Publish", Ct);
        }

        await using (var scope = Service(Checker))
        {
            await scope.Service.ApprovePublishAsync(proposed.Id, proposed.ContentHash, null, Ct);
        }

        return proposed.Id;
    }

    private sealed class FakePermissions(params (string Person, string Action)[] grants) : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) =>
            Task.FromResult(grants.Contains((subject, action)));
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
