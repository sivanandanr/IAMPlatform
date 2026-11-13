using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Domain.Access;
using IAM.Modules.Administration.Infrastructure.Access;
using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 2 stored access control (P0-013, DL-016, DL-059) that must behave the same on SQL Server and PostgreSQL:
/// the one-time bootstrap, second-person approval with its audit in the same transaction, refusals audited as denied,
/// one applied version per approval even when two approve at once, and decided requests and versions that no login can
/// change. The schema holds one model, so every test shares the bootstrap and uses its own people.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class AccessControlParityTests(ParityDatabase db)
{
    private const string Administrator = "access-admin";
    private const string Manager = "access-manager";
    private const string SecondAdministrator = "access-admin-2";
    private const string SecondManager = "access-manager-2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    // ---- Bootstrap ----------------------------------------------------------------------------------------------

    [Fact]
    public void MigrationsApplyOnceAndThenFindNothingToDo()
    {
        db.AdministrationFirstRunApplied.ShouldHaveSingleItem().ShouldEndWith("_AccessControl");
        db.AdministrationSecondRunPending.ShouldBeEmpty();
    }

    [Fact]
    public async Task BootstrapStoresTheDefaultsOnceAndASecondIsRefusedAndAudited()
    {
        await EnsureBootstrappedAsync();
        await using (var scope = Service("someone-else"))
        {
            var model = await scope.Service.CurrentModelAsync(Ct);
            model.Version.ShouldBeGreaterThanOrEqualTo(1);
            AccessModel.Defaults().Roles.Select(r => r.Code).Except(model.Roles.Select(r => r.Code)).ShouldBeEmpty();
            model.Can(Administrator, ActionCatalog.ChangesPropose).ShouldBeTrue();
            model.Can(Manager, ActionCatalog.ChangesApprove).ShouldBeTrue();
            model.Can(Administrator, ActionCatalog.ChangesApprove).ShouldBeFalse();
        }

        var deniedBefore = await AuditCountAsync(null, AccessAuditActions.Refused, "Denied");
        await using (var scope = Service("someone-else"))
        {
            (await Should.ThrowAsync<AccessRefusedException>(() => scope.Service.BootstrapAsync("x-admin", "x-manager", Ct)))
                .Refusal.ShouldBe(AccessRefusal.AlreadyBootstrapped);
        }

        (await AuditCountAsync(null, AccessAuditActions.Refused, "Denied")).ShouldBe(deniedBefore + 1);
        (await AuditCountAsync(null, AccessAuditActions.Bootstrapped, "Succeeded")).ShouldBe(1);
        (await ScalarLongAsync($"SELECT COUNT(*) FROM {T("AccessModelVersion")} WHERE {Col("Kind")} = 'Bootstrap'")).ShouldBe(1);
    }

    // ---- Second-person approval ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnApprovedChangeIsStoredWithItsVersionAndAuditTogether()
    {
        await EnsureBootstrappedAsync();
        var subject = NewSubject("auditor");
        var id = Guid.CreateVersion7();
        ChangeRequestView proposed;
        await using (var scope = Service(Administrator))
        {
            proposed = await scope.Service.ProposeAsync(id, new AssignRole(subject, AccessModel.Auditor), "Quarterly audit cover", Ct);
        }

        proposed.State.ShouldBe(ChangeState.Proposed);
        proposed.Maker.ShouldBe(Administrator);

        ChangeRequestView approved;
        await using (var scope = Service(Manager))
        {
            approved = await scope.Service.ApproveAsync(id, "Checked with the audit lead", Ct);
        }

        approved.State.ShouldBe(ChangeState.Approved);
        approved.Checker.ShouldBe(Manager);
        approved.AppliedAsVersion.ShouldNotBeNull();

        await using (var scope = Service("reader"))
        {
            var model = await scope.Service.CurrentModelAsync(Ct);
            model.RolesOf(subject).ShouldBe([AccessModel.Auditor]);
            model.Version.ShouldBeGreaterThanOrEqualTo(approved.AppliedAsVersion!.Value);
            var stored = (await scope.Service.FindChangeAsync(id, Ct)).ShouldNotBeNull();
            stored.ShouldBeEquivalentTo(approved);
        }

        (await ScalarLongAsync($"SELECT {Col("ModelVersion")} FROM {T("AccessModelVersion")} WHERE {Col("ChangeRequestId")} = '{id}'"))
            .ShouldBe(approved.AppliedAsVersion!.Value);
        (await AuditCountAsync(id, AccessAuditActions.Proposed, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(id, AccessAuditActions.Approved, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task TheMakerCannotApproveTheirOwnChangeAndTheRefusalIsAudited()
    {
        await EnsureBootstrappedAsync();
        var id = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(id, new AssignRole(NewSubject("self"), AccessModel.Auditor), "Cover", Ct);
        }

        await using (var scope = Service(Administrator))
        {
            (await Should.ThrowAsync<AccessRefusedException>(() => scope.Service.ApproveAsync(id, null, Ct))).Refusal
                .ShouldBeOneOf(AccessRefusal.MakerCannotDecide, AccessRefusal.NotAllowed);
        }

        (await AuditCountAsync(id, AccessAuditActions.Refused, "Denied")).ShouldBe(1);
        await using var check = Service("reader");
        (await check.Service.FindChangeAsync(id, Ct)).ShouldNotBeNull().State.ShouldBe(ChangeState.Proposed);
    }

    [Fact]
    public async Task NobodyCanGrantThemselvesARole()
    {
        await EnsureBootstrappedAsync();
        var id = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            (await Should.ThrowAsync<AccessRefusedException>(() =>
                scope.Service.ProposeAsync(id, new AssignRole(Administrator, AccessModel.Auditor), "Want it", Ct)))
                .Refusal.ShouldBe(AccessRefusal.SelfGrant);
        }

        (await AuditCountAsync(id, AccessAuditActions.Refused, "Denied")).ShouldBe(1);
        await using var check = Service("reader");
        (await check.Service.FindChangeAsync(id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task RejectAndWithdrawAreStoredAndChangeNothingElse()
    {
        await EnsureBootstrappedAsync();
        var rejected = Guid.CreateVersion7();
        var withdrawn = Guid.CreateVersion7();
        var subject = NewSubject("decided");
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(rejected, new AssignRole(subject, AccessModel.Auditor), "Cover", Ct);
            await scope.Service.ProposeAsync(withdrawn, new AssignRole(subject, AccessModel.PolicyAuthor), "Cover", Ct);
        }

        long versionBefore;
        await using (var scope = Service(Manager))
        {
            versionBefore = (await scope.Service.CurrentModelAsync(Ct)).Version;
            (await scope.Service.RejectAsync(rejected, "Not needed", Ct)).State.ShouldBe(ChangeState.Rejected);
        }

        await using (var scope = Service(Manager))
        {
            (await Should.ThrowAsync<AccessRefusedException>(() => scope.Service.WithdrawAsync(withdrawn, null, Ct))).Refusal
                .ShouldBe(AccessRefusal.NotMaker);
        }

        await using (var scope = Service(Administrator))
        {
            (await scope.Service.WithdrawAsync(withdrawn, "Raised by mistake", Ct)).State.ShouldBe(ChangeState.Withdrawn);
        }

        await using (var scope = Service(Manager))
        {
            (await Should.ThrowAsync<AccessRefusedException>(() => scope.Service.ApproveAsync(rejected, null, Ct))).Refusal
                .ShouldBe(AccessRefusal.NotProposed);
        }

        await using var check = Service("reader");
        var model = await check.Service.CurrentModelAsync(Ct);
        model.RolesOf(subject).ShouldBeEmpty();
        (await AuditCountAsync(rejected, AccessAuditActions.Rejected, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(withdrawn, AccessAuditActions.Withdrawn, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(withdrawn, AccessAuditActions.Refused, "Denied")).ShouldBe(1);
        (await AuditCountAsync(rejected, AccessAuditActions.Refused, "Denied")).ShouldBe(1);
        (await ScalarLongAsync($"SELECT COUNT(*) FROM {T("AccessModelVersion")} WHERE {Col("ChangeRequestId")} IN ('{rejected}', '{withdrawn}')")).ShouldBe(0);
        model.Version.ShouldBeGreaterThanOrEqualTo(versionBefore);
    }

    [Fact]
    public async Task RoleDefinitionsRoundTripAndRemovedActionsAreDeleted()
    {
        await EnsureBootstrappedAsync();
        var code = "reviewer-" + Guid.NewGuid().ToString("N")[..8];
        await ProposeAndApproveAsync(new DefineRole(code, "Reviewer", [ActionCatalog.ChangesRead, ActionCatalog.RolesRead]));

        await using (var scope = Service("reader"))
        {
            var role = (await scope.Service.CurrentModelAsync(Ct)).Roles.Single(r => r.Code == code);
            role.Name.ShouldBe("Reviewer");
            role.Actions.ShouldBe([ActionCatalog.ChangesRead, ActionCatalog.RolesRead], ignoreOrder: true);
        }

        await ProposeAndApproveAsync(new DefineRole(code, "Reviewer (read roles)", [ActionCatalog.RolesRead]));
        await using (var scope = Service("reader"))
        {
            var role = (await scope.Service.CurrentModelAsync(Ct)).Roles.Single(r => r.Code == code);
            role.Name.ShouldBe("Reviewer (read roles)");
            role.Actions.ShouldBe([ActionCatalog.RolesRead]);
        }

        (await ScalarLongAsync($"SELECT COUNT(*) FROM {T("AccessRoleAction")} WHERE {Col("RoleCode")} = '{code}'")).ShouldBe(1);
    }

    [Fact]
    public async Task SubjectsAreExactNotCaseInsensitive()
    {
        await EnsureBootstrappedAsync();
        var subject = NewSubject("Case");
        await ProposeAndApproveAsync(new AssignRole(subject, AccessModel.Auditor));
        await using var scope = Service("reader");
        var model = await scope.Service.CurrentModelAsync(Ct);
        model.RolesOf(subject).ShouldBe([AccessModel.Auditor]);
        model.RolesOf(subject.ToLowerInvariant()).ShouldBeEmpty();
        model.RolesOf(subject.ToUpperInvariant()).ShouldBeEmpty();
    }

    /// <summary>The audit writer refuses (the actions are not registered), so the proposal must not survive.</summary>
    [Fact]
    public async Task AFailedAuditWriteLeavesNoChange()
    {
        await EnsureBootstrappedAsync();
        var id = Guid.CreateVersion7();
        await using (var scope = Service(Administrator, registerAuditActions: false))
        {
            await Should.ThrowAsync<AuditRecordRejectedException>(() => scope.Service.ProposeAsync(id, new AssignRole(NewSubject("audit"), AccessModel.Auditor), "Cover", Ct));
        }

        await using var check = Service("reader");
        (await check.Service.FindChangeAsync(id, Ct)).ShouldBeNull();
    }

    // ---- Concurrency --------------------------------------------------------------------------------------------

    /// <summary>
    /// Two managers approve two different changes at the same moment. Either both apply in turn with different versions,
    /// or one is refused as a conflict and nothing of it is stored. Never two changes under one version.
    /// </summary>
    [Fact]
    public async Task TwoApprovalsAtOnceNeverShareAVersion()
    {
        await EnsureBootstrappedAsync();
        for (var round = 0; round < 5; round++)
        {
            var first = Guid.CreateVersion7();
            var second = Guid.CreateVersion7();
            var firstSubject = NewSubject("race-a");
            var secondSubject = NewSubject("race-b");
            await using (var scope = Service(Administrator))
            {
                await scope.Service.ProposeAsync(first, new AssignRole(firstSubject, AccessModel.Auditor), "Race", Ct);
                await scope.Service.ProposeAsync(second, new AssignRole(secondSubject, AccessModel.Auditor), "Race", Ct);
            }

            var outcomes = await Task.WhenAll(ApproveOrConflictAsync(Manager, first), ApproveOrConflictAsync(SecondManager, second));
            outcomes.Count(o => o is not null).ShouldBeGreaterThanOrEqualTo(1);
            var applied = outcomes.Where(o => o is not null).Select(o => o!.Value).ToList();
            applied.Distinct().Count().ShouldBe(applied.Count);

            await using var check = Service("reader");
            var model = await check.Service.CurrentModelAsync(Ct);
            model.RolesOf(firstSubject).Count.ShouldBe(outcomes[0] is null ? 0 : 1);
            model.RolesOf(secondSubject).Count.ShouldBe(outcomes[1] is null ? 0 : 1);
            (await check.Service.FindChangeAsync(first, Ct))!.State.ShouldBe(outcomes[0] is null ? ChangeState.Proposed : ChangeState.Approved);
            (await check.Service.FindChangeAsync(second, Ct))!.State.ShouldBe(outcomes[1] is null ? ChangeState.Proposed : ChangeState.Approved);
        }
    }

    [Fact]
    public async Task TheSameChangeApprovedTwiceAtOnceAppliesOnce()
    {
        await EnsureBootstrappedAsync();

        // Several rounds, so the loser sometimes reads the request before the winner commits and sometimes after: it must
        // then get "not proposed" or a conflict, never an unexpected database error.
        for (var round = 0; round < 10; round++)
        {
            var id = Guid.CreateVersion7();
            await using (var scope = Service(Administrator))
            {
                await scope.Service.ProposeAsync(id, new AssignRole(NewSubject("twice"), AccessModel.Auditor), "Twice", Ct);
            }

            var outcomes = await Task.WhenAll(ApproveOrRefusedAsync(Manager, id), ApproveOrRefusedAsync(SecondManager, id));
            outcomes.Count(o => o).ShouldBe(1);
            (await ScalarLongAsync($"SELECT COUNT(*) FROM {T("AccessModelVersion")} WHERE {Col("ChangeRequestId")} = '{id}'")).ShouldBe(1);
            (await AuditCountAsync(id, AccessAuditActions.Approved, "Succeeded")).ShouldBe(1);
        }
    }

    // ---- Database guarantees, whatever the application does ---------------------------------------------------------

    [Fact]
    public async Task DecidedRequestsAndVersionsCannotBeChangedOrRemoved()
    {
        await EnsureBootstrappedAsync();
        var id = await ProposeAndApproveAsync(new AssignRole(NewSubject("frozen"), AccessModel.Auditor));
        var app = db.AdministrationAppConnectionString;

        await ShouldBeImmutableAsync(app, $"UPDATE {T("AccessChangeRequest")} SET {Col("DecisionReason")} = 'edited' WHERE {Col("Id")} = '{id}'");
        await ShouldBeImmutableAsync(app, $"UPDATE {T("AccessChangeRequest")} SET {Col("State")} = 'Proposed', {Col("Checker")} = NULL, {Col("DecidedAtUtc")} = NULL, {Col("AppliedAsVersion")} = NULL, {Col("DecisionReason")} = NULL WHERE {Col("Id")} = '{id}'");
        await ShouldNotDeleteLinkedRowAsync(app, $"DELETE FROM {T("AccessChangeRequest")} WHERE {Col("Id")} = '{id}'");
        await ShouldBeImmutableAsync(app, $"UPDATE {T("AccessModelVersion")} SET {Col("AppliedBy")} = 'someone' WHERE {Col("ChangeRequestId")} = '{id}'");
        await ShouldNotDeleteLinkedRowAsync(app, $"DELETE FROM {T("AccessModelVersion")} WHERE {Col("ChangeRequestId")} = '{id}'");

        // The schema owner is bound by the triggers too.
        await ShouldNotDeleteLinkedRowAsync(db.MigratorConnectionString, $"DELETE FROM {T("AccessModelVersion")} WHERE {Col("ChangeRequestId")} = '{id}'");
        await ShouldNotDeleteLinkedRowAsync(db.MigratorConnectionString, $"DELETE FROM {T("AccessChangeRequest")} WHERE {Col("Id")} = '{id}'");
        // TRUNCATE skips row triggers; both tables are referenced by a foreign key, which refuses it on both engines
        // (PostgreSQL also has a statement trigger).
        foreach (var table in new[] { "AccessModelVersion", "AccessChangeRequest" })
        {
            var truncate = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE TABLE {T(table)}", Ct));
            (truncate.Message.Contains("immutable", StringComparison.OrdinalIgnoreCase) || truncate.Message.Contains("foreign key", StringComparison.OrdinalIgnoreCase))
                .ShouldBeTrue(truncate.Message);
        }

        await using var check = Service("reader");
        (await check.Service.FindChangeAsync(id, Ct)).ShouldNotBeNull().State.ShouldBe(ChangeState.Approved);
    }

    [Fact]
    public async Task WhatWasProposedCannotBeEditedWhileItWaits()
    {
        await EnsureBootstrappedAsync();
        var id = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(id, new AssignRole(NewSubject("waiting"), AccessModel.Auditor), "Cover", Ct);
        }

        var app = db.AdministrationAppConnectionString;
        await ShouldBeImmutableAsync(app, $"UPDATE {T("AccessChangeRequest")} SET {Col("Payload")} = '{{\"subject\":\"{Administrator}\",\"roleCode\":\"auditor\"}}' WHERE {Col("Id")} = '{id}'");
        await ShouldBeImmutableAsync(app, $"UPDATE {T("AccessChangeRequest")} SET {Col("Maker")} = '{Manager}' WHERE {Col("Id")} = '{id}'");
        await ShouldBeImmutableAsync(app, $"UPDATE {T("AccessChangeRequest")} SET {Col("Kind")} = 'RemoveAssignment' WHERE {Col("Id")} = '{id}'");
        await ShouldBeImmutableAsync(app, $"DELETE FROM {T("AccessChangeRequest")} WHERE {Col("Id")} = '{id}'");
    }

    [Theory]
    [InlineData("Approved", "access-admin", "access-manager", "'{}'", "AssignRole", "NULL", "State")]
    [InlineData("Rejected", "access-admin", "access-manager", "'{}'", "AssignRole", "NULL", "State")]
    [InlineData("Withdrawn", "access-admin", "access-manager", "'{}'", "AssignRole", "NULL", "State")]
    [InlineData("Withdrawn", "access-admin", null, "'{}'", "AssignRole", "NULL", "State")]
    [InlineData("Proposed", "access-admin", "access-manager", "'{}'", "AssignRole", "NULL", "State")]
    [InlineData("Granted", "access-admin", null, "'{}'", "AssignRole", "NULL", "State")]
    [InlineData("Proposed", "access-admin", null, "'not json'", "AssignRole", "NULL", "Payload_Json")]
    [InlineData("Proposed", "access-admin", null, "'1'", "AssignRole", "NULL", "Payload_Json")]
    [InlineData("Proposed", "access-admin", null, "'[]'", "AssignRole", "NULL", "Payload_Json")]
    [InlineData("Proposed", "access-admin", null, "'{}'", "GrantEverything", "NULL", "Kind_Known")]
    public async Task ImpossibleRequestsAreRefusedByTheDatabase(string state, string maker, string? checker, string payloadLiteral, string kind, string appliedAs, string constraint)
    {
        // Each row breaks exactly one rule: everything else matches its state, so the CHECK can't refuse it for another reason.
        var decided = state == "Proposed" ? "NULL" : "'2026-11-10T00:00:00+00:00'";

        string[] columns =
        [
            "Id", "Kind", "Payload", "Maker", "Reason", "ProposedAtUtc", "ProposedAgainstVersion", "State", "Checker", "DecisionReason",
            "DecidedAtUtc", "AppliedAsVersion", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version",
        ];
        string[] values =
        [
            $"'{Guid.CreateVersion7()}'", $"'{kind}'", payloadLiteral, $"'{maker}'", "'r'", "'2026-11-10T00:00:00+00:00'", "1", $"'{state}'",
            checker is null ? "NULL" : $"'{checker}'", "NULL", decided, appliedAs, "'2026-11-10T00:00:00+00:00'", "'t'", "'2026-11-10T00:00:00+00:00'", "'t'", "1",
        ];
        var sql = $"INSERT INTO {T("AccessChangeRequest")} ({string.Join(", ", columns.Select(Col))}) VALUES ({string.Join(", ", values)})";
        await ShouldViolateConstraintAsync(sql, $"CK_AccessChangeRequest_{constraint}");
    }

    /// <summary>
    /// Rule 3 at database level: a request can't be approved or rejected by its maker, even by SQL. Each row is otherwise
    /// valid (a real version for the approval), so only the self-decision clause can refuse it; rolled back either way.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesSelfDecisionsOnItsOwn()
    {
        await EnsureBootstrappedAsync();
        var next = await ScalarLongAsync($"SELECT MAX({Col("ModelVersion")}) + 1 FROM {T("AccessModelVersion")}");
        var approved = Guid.CreateVersion7();
        var approve = $"{InsertProposedRequestSql(approved)}; "
            + InsertVersionSql($"{next}, 'Change', '{approved}', '{Hash}', 't', '2026-11-10T00:00:00+00:00'") + "; "
            + $"UPDATE {T("AccessChangeRequest")} SET {Col("State")} = 'Approved', {Col("Checker")} = '{Administrator}', {Col("DecidedAtUtc")} = '2026-11-10T00:00:00+00:00', {Col("AppliedAsVersion")} = {next} WHERE {Col("Id")} = '{approved}'";
        await ShouldViolateConstraintAsync(RolledBack(approve), "CK_AccessChangeRequest_State");

        var rejected = Guid.CreateVersion7();
        var reject = $"{InsertProposedRequestSql(rejected)}; "
            + $"UPDATE {T("AccessChangeRequest")} SET {Col("State")} = 'Rejected', {Col("Checker")} = '{Administrator}', {Col("DecisionReason")} = 'mine', {Col("DecidedAtUtc")} = '2026-11-10T00:00:00+00:00' WHERE {Col("Id")} = '{rejected}'";
        await ShouldViolateConstraintAsync(RolledBack(reject), "CK_AccessChangeRequest_State");
    }

    /// <summary>
    /// Deterministic approve-versus-reject race: the reject commits just before the approval claims its version. The
    /// database refuses the claim (the request is no longer proposed) and the approval reports a conflict on both engines.
    /// </summary>
    [Fact]
    public async Task ARejectCommittedJustBeforeTheClaimTurnsTheApprovalIntoAConflict()
    {
        await EnsureBootstrappedAsync();
        var id = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(id, new AssignRole(NewSubject("raced"), AccessModel.Auditor), "Cover", Ct);
        }

        var interceptor = new BeforeVersionInsert(async () =>
        {
            await using var rejecter = Service(SecondManager);
            await rejecter.Service.RejectAsync(id, "Not needed", Ct);
        });
        await using (var scope = Service(Manager, extra: interceptor))
        {
            await Should.ThrowAsync<AccessConflictException>(() => scope.Service.ApproveAsync(id, null, Ct));
        }

        interceptor.Fired.ShouldBeTrue();
        await using var check = Service("reader");
        (await check.Service.FindChangeAsync(id, Ct)).ShouldNotBeNull().State.ShouldBe(ChangeState.Rejected);
        (await ScalarLongAsync($"SELECT COUNT(*) FROM {T("AccessModelVersion")} WHERE {Col("ChangeRequestId")} = '{id}'")).ShouldBe(0);
        (await AuditCountAsync(id, AccessAuditActions.Approved, "Succeeded")).ShouldBe(0);
    }

    /// <summary>
    /// A version claimed for a request that never became approved (written outside the service, with the right hash) is
    /// caught on read. Done inside a transaction that is rolled back, so the shared schema is untouched. The integrity
    /// audit can't be written on that connection, which also shows the failure is still the one reported.
    /// </summary>
    [Fact]
    [SuppressMessage("Security", "CA2100", Justification = "Test: constant SQL built from GUIDs and numbers; no user input.")]
    public async Task AVersionWhoseRequestWasNeverApprovedIsCaughtOnRead()
    {
        await EnsureBootstrappedAsync();
        var id = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(id, new AssignRole(NewSubject("stray"), AccessModel.Auditor), "Cover", Ct);
        }

        var latest = await ScalarLongAsync($"SELECT MAX({Col("ModelVersion")}) FROM {T("AccessModelVersion")}");
        var hash = (string)(await db.ScalarAsync(db.AdministrationAppConnectionString, $"SELECT {Col("ModelHash")} FROM {T("AccessModelVersion")} WHERE {Col("ModelVersion")} = {latest}", Ct))!;
        await using var connection = db.OpenConnection(db.AdministrationAppConnectionString);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = InsertVersionSql($"{latest + 1}, 'Change', '{id}', '{hash}', 't', '2026-11-10T00:00:00+00:00'");
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var context = TestSupport.CreateAdministration(db.Provider, db.AdministrationAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor("reader"))))
        {
            context.Database.SetDbConnection(connection);
            await context.Database.UseTransactionAsync(tx, Ct);
            var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor("reader"), new AuditActionCatalog(AccessAuditActions.All.Select(a => new AuditActionRegistration(a))));
            var service = new AccessAdministrationService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor("reader"));
            var ex = await Should.ThrowAsync<AccessStoreIntegrityException>(() => service.CurrentModelAsync(Ct));
            ex.Check.ShouldBe("link");
            ex.AuditFailure.ShouldNotBeNull("the audit can't open a second transaction here, and must not hide the integrity failure");
        }

        await tx.RollbackAsync(Ct);
    }

    [Fact]
    public async Task ATamperedStoredChangeIsRefusedOnRead()
    {
        await EnsureBootstrappedAsync();
        var id = Guid.CreateVersion7();
        string[] columns =
        [
            "Id", "Kind", "Payload", "Maker", "Reason", "ProposedAtUtc", "ProposedAgainstVersion", "State",
            "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version",
        ];
        string[] values =
        [
            $"'{id}'", "'AssignRole'", "'{\"subject\":\"x\",\"roleCode\":\"auditor\",\"grantAll\":true}'", $"'{Administrator}'", "'r'",
            "'2026-11-10T00:00:00+00:00'", "1", "'Proposed'", "'2026-11-10T00:00:00+00:00'", "'t'", "'2026-11-10T00:00:00+00:00'", "'t'", "1",
        ];
        await db.ExecuteAsync(db.AdministrationAppConnectionString, $"INSERT INTO {T("AccessChangeRequest")} ({string.Join(", ", columns.Select(Col))}) VALUES ({string.Join(", ", values)})", Ct);

        await using (var scope = Service(Manager))
        {
            await Should.ThrowAsync<AccessStoreIntegrityException>(() => scope.Service.FindChangeAsync(id, Ct));
        }

        await using (var scope = Service(Manager))
        {
            await Should.ThrowAsync<AccessStoreIntegrityException>(() => scope.Service.ApproveAsync(id, null, Ct));
        }

        // Audited as a failure each time, never as an ordinary refusal; nothing else was stored.
        (await AuditCountAsync(id, AccessAuditActions.IntegrityFailed, "Failed")).ShouldBe(2);
        (await AuditCountAsync(id, AccessAuditActions.Refused, "Denied")).ShouldBe(0);
        (await ScalarLongAsync($"SELECT COUNT(*) FROM {T("AccessModelVersion")} WHERE {Col("ChangeRequestId")} = '{id}'")).ShouldBe(0);
    }

    [Fact]
    public async Task OnlyOneBootstrapVersionCanExist()
    {
        await EnsureBootstrappedAsync();
        // A second bootstrap row: the CHECK (bootstrap is version 1) refuses it on PostgreSQL; on SQL Server the unique
        // index on ChangeRequestId, which allows a single NULL, refuses it first. Either way there is one bootstrap.
        await ShouldViolateConstraintAsync(
            InsertVersionSql($"1000000, 'Bootstrap', NULL, '{Hash}', 't', '2026-11-10T00:00:00+00:00'"),
            "CK_AccessModelVersion_Kind",
            "IX_AccessModelVersion_ChangeRequestId");
        // A change version with no request: the CHECK on PostgreSQL; the same single-NULL index first on SQL Server.
        await ShouldViolateConstraintAsync(
            InsertVersionSql($"1000001, 'Change', NULL, '{Hash}', 't', '2026-11-10T00:00:00+00:00'"),
            "CK_AccessModelVersion_Kind",
            "IX_AccessModelVersion_ChangeRequestId");
    }

    // ---- Review fixes: history linked to requests, hash checked on read, revoked makers ---------------------------------

    [Fact]
    public async Task AVersionMustNameARealProposedRequestWithAValidHash()
    {
        await EnsureBootstrappedAsync();
        var app = db.AdministrationAppConnectionString;

        // No such request: refused by the foreign key (SQL Server) or first by the claim trigger (PostgreSQL, BEFORE INSERT).
        var missing = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(app, InsertVersionSql($"1000002, 'Change', '{Guid.CreateVersion7()}', '{Hash}', 't', '2026-11-10T00:00:00+00:00'"), Ct));
        (missing is SqlException { Number: 547 } || missing is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } || missing.Message.Contains("proposed", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue(missing.Message);

        // A decided request can't be given another version.
        var rejected = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(rejected, new AssignRole(NewSubject("late"), AccessModel.Auditor), "Cover", Ct);
        }

        await using (var scope = Service(Manager))
        {
            await scope.Service.RejectAsync(rejected, "No", Ct);
        }

        var ex = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(app, InsertVersionSql($"1000003, 'Change', '{rejected}', '{Hash}', 't', '2026-11-10T00:00:00+00:00'"), Ct));
        ex.Message.ShouldContain("proposed", Case.Insensitive);

        // A hash must be 64 lower-case hex characters. The row is otherwise valid (a proposed request, the next number), so
        // only the hash CHECK can refuse it; the insert is rolled back in case it doesn't, so later tests aren't affected.
        var proposed = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(proposed, new AssignRole(NewSubject("hash"), AccessModel.Auditor), "Cover", Ct);
        }

        var next = await ScalarLongAsync($"SELECT MAX({Col("ModelVersion")}) + 1 FROM {T("AccessModelVersion")}");
        foreach (var bad in new[] { "v1:" + new string('A', 64), "v1:" + new string('a', 63), "v1:" + new string('g', 64), new string('a', 64), "v2:" + new string('a', 64) })
        {
            var insert = InsertVersionSql($"{next}, 'Change', '{proposed}', '{bad}', 't', '2026-11-10T00:00:00+00:00'");
            await ShouldViolateConstraintAsync(RolledBack(insert), "CK_AccessModelVersion_ModelHash_Hex");
        }
    }

    [Fact]
    public async Task AProposalReusingATakenIdIsAConflictAndAuditsNothingAgainstIt()
    {
        await EnsureBootstrappedAsync();
        var id = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(id, new AssignRole(NewSubject("first"), AccessModel.Auditor), "Cover", Ct);
        }

        // Even a proposal that would be refused (here, a self-grant) must not file a refusal against the existing request.
        await using (var scope = Service(SecondAdministrator))
        {
            await Should.ThrowAsync<AccessConflictException>(() => scope.Service.ProposeAsync(id, new AssignRole(SecondAdministrator, AccessModel.Auditor), "Mine", Ct));
        }

        (await AuditCountAsync(id, AccessAuditActions.Refused, "Denied")).ShouldBe(0);
        (await AuditCountAsync(id, AccessAuditActions.Proposed, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task AnApprovedRequestMustNameTheVersionThatNamesIt()
    {
        await EnsureBootstrappedAsync();
        // The waiting request is proposed first, so the other request's version is newer than it was proposed against and
        // the state CHECK accepts it: only the link can refuse.
        var waiting = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(waiting, new AssignRole(NewSubject("unlinked"), AccessModel.Auditor), "Cover", Ct);
        }

        var approved = await ProposeAndApproveAsync(new AssignRole(NewSubject("linked"), AccessModel.Auditor));

        // Marking the waiting request approved "as" the other request's version is refused: that version names another request.
        var version = await ScalarLongAsync($"SELECT {Col("AppliedAsVersion")} FROM {T("AccessChangeRequest")} WHERE {Col("Id")} = '{approved}'");
        await ShouldViolateConstraintAsync(
            $"UPDATE {T("AccessChangeRequest")} SET {Col("State")} = 'Approved', {Col("Checker")} = '{Manager}', {Col("DecidedAtUtc")} = '2026-11-10T00:00:00+00:00', {Col("AppliedAsVersion")} = {version} WHERE {Col("Id")} = '{waiting}'",
            "FK_AccessChangeRequest_AccessModelVersion_Applied");
    }

    [Fact]
    public async Task EveryVersionStoresTheHashOfTheModelItProduced()
    {
        await EnsureBootstrappedAsync();
        var id = await ProposeAndApproveAsync(new AssignRole(NewSubject("hashed"), AccessModel.Auditor));
        await using var scope = Service("reader");
        var model = await scope.Service.CurrentModelAsync(Ct);
        var stored = await db.ScalarAsync(db.AdministrationAppConnectionString, $"SELECT {Col("ModelHash")} FROM {T("AccessModelVersion")} WHERE {Col("ModelVersion")} = {model.Version}", Ct);
        stored.ShouldBe("v1:" + model.ContentHash());
        (await ScalarLongAsync($"SELECT COUNT(*) FROM {T("AccessModelVersion")} WHERE {Col("ChangeRequestId")} = '{id}'")).ShouldBe(1);
    }

    /// <summary>A role assignment written straight into the table (R-033) no longer matches the version's hash, so reads fail and are audited.</summary>
    [Fact]
    public async Task ATableEditedOutsideTheApplicationIsDetectedOnRead()
    {
        await EnsureBootstrappedAsync();
        var intruder = NewSubject("intruder");
        string[] columns = ["Subject", "RoleCode", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version"];
        var insert = $"INSERT INTO {T("AccessRoleAssignment")} ({string.Join(", ", columns.Select(Col))}) VALUES ('{intruder}', 'auditor', '2026-11-10T00:00:00+00:00', 't', '2026-11-10T00:00:00+00:00', 't', 1)";
        var failedBefore = await AuditCountAsync(null, AccessAuditActions.IntegrityFailed, "Failed");
        var blocked = Guid.CreateVersion7();
        await db.ExecuteAsync(db.AdministrationAppConnectionString, insert, Ct);
        try
        {
            await using (var scope = Service("reader"))
            {
                await Should.ThrowAsync<AccessStoreIntegrityException>(() => scope.Service.CurrentModelAsync(Ct));
            }

            await using (var scope = Service(Administrator))
            {
                await Should.ThrowAsync<AccessStoreIntegrityException>(() =>
                    scope.Service.ProposeAsync(blocked, new AssignRole(NewSubject("blocked"), AccessModel.Auditor), "Cover", Ct));
            }

            (await AuditCountAsync(null, AccessAuditActions.IntegrityFailed, "Failed")).ShouldBe(failedBefore + 1);
            (await AuditCountAsync(blocked, AccessAuditActions.IntegrityFailed, "Failed")).ShouldBe(1);
        }
        finally
        {
            await db.ExecuteAsync(db.AdministrationAppConnectionString, $"DELETE FROM {T("AccessRoleAssignment")} WHERE {Col("Subject")} = '{intruder}'", Ct);
        }

        await using var check = Service("reader");
        (await check.Service.CurrentModelAsync(Ct)).RolesOf(intruder).ShouldBeEmpty();
    }

    [Fact]
    public async Task AProposalWhoseMakerLostTheRightToProposeIsNotApproved()
    {
        await EnsureBootstrappedAsync();
        var leaver = NewSubject("leaver");
        await ProposeAndApproveAsync(new AssignRole(leaver, AccessModel.IamAdministrator));
        var id = Guid.CreateVersion7();
        await using (var scope = Service(leaver))
        {
            await scope.Service.ProposeAsync(id, new AssignRole(NewSubject("leaver-pick"), AccessModel.Auditor), "Cover", Ct);
        }

        await ProposeAndApproveAsync(new RemoveAssignment(leaver, AccessModel.IamAdministrator));
        await using (var scope = Service(Manager))
        {
            (await Should.ThrowAsync<AccessRefusedException>(() => scope.Service.ApproveAsync(id, null, Ct))).Refusal.ShouldBe(AccessRefusal.MakerNoLongerAllowed);
        }

        (await AuditCountAsync(id, AccessAuditActions.Refused, "Denied")).ShouldBe(1);
        await using (var scope = Service(Manager))
        {
            (await scope.Service.RejectAsync(id, "Maker left", Ct)).State.ShouldBe(ChangeState.Rejected);
        }
    }

    [Fact]
    public async Task DecisionsRecordTheModelVersionTheyWereMadeAgainst()
    {
        await EnsureBootstrappedAsync();
        long before;
        await using (var scope = Service("reader"))
        {
            before = (await scope.Service.CurrentModelAsync(Ct)).Version;
        }

        var id = await ProposeAndApproveAsync(new AssignRole(NewSubject("evidence"), AccessModel.Auditor));
        var sql = IsSqlServer
            ? $"SELECT [PolicyVersionRef] FROM [audit].[AuditEvent] WHERE [TargetId] = '{id}' AND [Action] = '{AccessAuditActions.Approved}'"
            : $"SELECT policy_version_ref FROM audit.audit_event WHERE target_id = '{id}' AND action = '{AccessAuditActions.Approved}'";
        (await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct)).ShouldBe($"access-model:{before}");
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The first test to run bootstraps; the rest find it done. A second manager and administrator then join through the
    /// normal approval path, so the concurrency tests have two different people who can approve. Each step is checked on
    /// its own, so a run that stopped half way is completed rather than skipped. Tests in one collection run one at a time.
    /// </summary>
    private async Task EnsureBootstrappedAsync()
    {
        AccessModel model;
        await using (var scope = Service("bootstrap-operator"))
        {
            model = await scope.Service.CurrentModelAsync(Ct);
            if (model.Version == 0)
            {
                await scope.Service.BootstrapAsync(Administrator, Manager, Ct);
            }
        }

        if (!model.RolesOf(SecondManager).Contains(AccessModel.IamManager))
        {
            await ProposeAndApproveAsync(new AssignRole(SecondManager, AccessModel.IamManager));
        }

        if (!model.RolesOf(SecondAdministrator).Contains(AccessModel.IamAdministrator))
        {
            await ProposeAndApproveAsync(new AssignRole(SecondAdministrator, AccessModel.IamAdministrator));
        }
    }

    private const string Hash = "v1:0000000000000000000000000000000000000000000000000000000000000000";

    private string RolledBack(string statements) => IsSqlServer ? $"BEGIN TRAN; {statements}; ROLLBACK;" : $"BEGIN; {statements}; ROLLBACK;";

    private string InsertProposedRequestSql(Guid id)
    {
        string[] columns =
        [
            "Id", "Kind", "Payload", "Maker", "Reason", "ProposedAtUtc", "ProposedAgainstVersion", "State",
            "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version",
        ];
        string[] values =
        [
            $"'{id}'", "'AssignRole'", "'{\"subject\":\"x\",\"roleCode\":\"auditor\"}'", $"'{Administrator}'", "'r'",
            "'2026-11-10T00:00:00+00:00'", "1", "'Proposed'", "'2026-11-10T00:00:00+00:00'", "'t'", "'2026-11-10T00:00:00+00:00'", "'t'", "1",
        ];
        return $"INSERT INTO {T("AccessChangeRequest")} ({string.Join(", ", columns.Select(Col))}) VALUES ({string.Join(", ", values)})";
    }

    private string InsertVersionSql(string values)
    {
        string[] columns = ["ModelVersion", "Kind", "ChangeRequestId", "ModelHash", "AppliedBy", "AppliedAtUtc"];
        return $"INSERT INTO {T("AccessModelVersion")} ({string.Join(", ", columns.Select(Col))}) VALUES ({values})";
    }

    /// <summary>
    /// Refused by the named CHECK, unique or foreign-key constraint (SQL Server spelling; the PostgreSQL name is its
    /// snake_case form), so a row that breaks some other rule can't make the test pass.
    /// </summary>
    private async Task ShouldViolateConstraintAsync(string constantSql, params string[] constraints)
    {
        var ex = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.AdministrationAppConnectionString, constantSql, Ct));
        var violation = constraints.Any(constraint =>
        {
            var pgName = string.Join('_', constraint.Split('_').Select((part, i) => i == 0 ? part.ToLowerInvariant() : IamModelConventions.ToSnakeCase(part)));
            return ex switch
            {
                // 547 names its constraint in quotes; 2601 (unique index) names the index in single quotes.
                SqlException sql => (sql.Number is 547 or 2627 && sql.Message.Contains($"\"{constraint}\"", StringComparison.Ordinal))
                    || (sql.Number is 2601 or 2627 && sql.Message.Contains($"'{constraint}'", StringComparison.Ordinal)),
                PostgresException pg => pg.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation
                    && pg.ConstraintName == pgName,
                _ => false,
            };
        });
        violation.ShouldBeTrue($"expected {string.Join(" or ", constraints)} to refuse: {constantSql}, got {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Runs an action once, just before the first INSERT into the model version table on this context.</summary>
    private sealed class BeforeVersionInsert(Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await FireAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await FireAsync(command);
            return result;
        }

        private async Task FireAsync(DbCommand command)
        {
            var text = command.CommandText;
            if (text.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("[AccessModelVersion]", StringComparison.Ordinal) || text.Contains("access_model_version", StringComparison.Ordinal))
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await action();
            }
        }
    }

    private async Task<Guid> ProposeAndApproveAsync(AccessChange change)
    {
        var id = Guid.CreateVersion7();
        await using (var scope = Service(Administrator))
        {
            await scope.Service.ProposeAsync(id, change, "Parity test change", Ct);
        }

        await using (var scope = Service(Manager))
        {
            await scope.Service.ApproveAsync(id, null, Ct);
        }

        return id;
    }

    private async Task<long?> ApproveOrConflictAsync(string checker, Guid id)
    {
        await using var scope = Service(checker);
        try
        {
            return (await scope.Service.ApproveAsync(id, null, Ct)).AppliedAsVersion;
        }
        catch (AccessConflictException)
        {
            return null;
        }
    }

    private async Task<bool> ApproveOrRefusedAsync(string checker, Guid id)
    {
        await using var scope = Service(checker);
        try
        {
            await scope.Service.ApproveAsync(id, null, Ct);
            return true;
        }
        catch (AccessConflictException)
        {
            return false;
        }
        catch (AccessRefusedException ex) when (ex.Refusal == AccessRefusal.NotProposed)
        {
            return false;
        }
    }

    /// <summary>
    /// An approved request and its version row reference each other, so SQL Server's foreign keys refuse the delete before
    /// the AFTER trigger runs; PostgreSQL's BEFORE trigger refuses it first. Either way the row stays.
    /// </summary>
    private async Task ShouldNotDeleteLinkedRowAsync(string connectionString, string constantSql)
    {
        var ex = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(connectionString, constantSql, Ct));
        (ex is SqlException { Number: 547 } || ex is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation }
            || ex.Message.Contains("immutable", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue(ex.Message);
    }

    private async Task ShouldBeImmutableAsync(string connectionString, string constantSql)
    {
        var ex = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(connectionString, constantSql, Ct));
        ex.Message.ShouldContain("immutable", Case.Insensitive, constantSql);
    }

    private static string NewSubject(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}@example.test";

    private string T(string pascal) => IsSqlServer ? $"[configuration].[{pascal}]" : $"configuration.{IamModelConventions.ToSnakeCase(pascal)}";

    private string Col(string pascal) => IsSqlServer ? $"[{pascal}]" : IamModelConventions.ToSnakeCase(pascal);

    private async Task<long> ScalarLongAsync(string constantSql) =>
        Convert.ToInt64(await db.ScalarAsync(db.AdministrationAppConnectionString, constantSql, Ct), CultureInfo.InvariantCulture);

    private async Task<long> AuditCountAsync(Guid? targetId, string action, string outcome)
    {
        var target = targetId is null
            ? (IsSqlServer ? "[TargetType] = 'administration.access_model'" : "target_type = 'administration.access_model'")
            : (IsSqlServer ? $"[TargetId] = '{targetId}'" : $"target_id = '{targetId}'");
        var sql = IsSqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE {target} AND [Action] = '{action}' AND [Outcome] = '{outcome}'"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE {target} AND action = '{action}' AND outcome = '{outcome}'";
        return Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }

    private ServiceScope Service(string actor, bool registerAuditActions = true, IInterceptor? extra = null)
    {
        var context = TestSupport.CreateAdministration(
            db.Provider,
            db.AdministrationAppConnectionString,
            new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)),
            extra);
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider),
            TimeProvider.System,
            new FixedActor(actor),
            new AuditActionCatalog(registerAuditActions ? AccessAuditActions.All.Select(a => new AuditActionRegistration(a)) : []));
        return new ServiceScope(context, new AccessAdministrationService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor)));
    }

    private sealed class ServiceScope(AdministrationDbContext context, AccessAdministrationService service) : IAsyncDisposable
    {
        public AccessAdministrationService Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
