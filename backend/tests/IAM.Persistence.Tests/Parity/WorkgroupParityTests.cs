using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Organization.Application.Workgroups;
using IAM.Modules.Organization.Domain.Workgroups;
using IAM.Modules.Organization.Infrastructure.Persistence;
using IAM.Modules.Organization.Infrastructure.Workgroups;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Workgroups on SQL Server and PostgreSQL (DL-068): members activated by a second person who is not a member, every
/// refused write audited, the fallback workgroup seeded, the directory contract, and the database refusing the same by SQL.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class WorkgroupParityTests(ParityDatabase db)
{
    private const string Author = "wg-admin";
    private const string Checker = "wg-checker";

    private static readonly FakePermissions DefaultPermissions = new(
        (Author, WorkgroupAuditActions.ManageAction), (Author, WorkgroupAuditActions.ReadAction),
        ("wg-admin-2", WorkgroupAuditActions.ManageAction), ("wg-admin-2", WorkgroupAuditActions.ReadAction),
        (Checker, WorkgroupAuditActions.ApproveAction), (Checker, WorkgroupAuditActions.ReadAction),
        ("alice", WorkgroupAuditActions.ApproveAction), ("alice", WorkgroupAuditActions.ReadAction));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task TheMigrationAppliesOnceAndSeedsTheFallbackWorkgroup()
    {
        db.OrganizationFirstRunApplied.ShouldHaveSingleItem().ShouldEndWith("_Workgroups");
        db.OrganizationSecondRunPending.ShouldBeEmpty();
        await using var scope = Service();
        var fallback = (await scope.Service.FindAsync(WorkgroupRules.FallbackKey, Ct)).ShouldNotBeNull();
        fallback.Id.ShouldBe(WorkgroupRules.FallbackId);
        fallback.Draft.ShouldNotBeNull().ContentHash.ShouldBe(WorkgroupVersion.ComputeContentHash(WorkgroupRules.FallbackKey, 1, []));
        (await Directory().ExistsAsync(WorkgroupRules.FallbackKey, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task MembersAreActivatedByASecondPersonWhoIsNotAMember()
    {
        var registered = await RegisterAsync(NewKey("fin"), ["alice", "bob"]);
        (await Directory().FindActiveAsync(registered.Key, Ct)).ShouldBeNull("nothing is active until approved");
        var proposed = await ProposeAsync(registered.Id);

        await using (var scope = Service("alice"))
        {
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(WorkgroupRefusal.MemberCannotDecide);
        }

        await using (var scope = Service(Checker))
        {
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, new string('0', 64), null, Ct)))
                .Refusal.ShouldBe(WorkgroupRefusal.DraftChanged);
            (await scope.Service.ReviewRequestAsync(proposed.Id, Ct)).ShouldNotBeNull().Draft.ShouldNotBeNull().Members.ShouldBe(["alice", "bob"]);
            (await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, "Checked", Ct)).State.ShouldBe("Approved");
        }

        var active = (await Directory().FindActiveAsync(registered.Key, Ct)).ShouldNotBeNull();
        active.Members.ShouldBe(["alice", "bob"]);
        active.VersionNumber.ShouldBe(1);
        (await AuditCountAsync(registered.Id, WorkgroupAuditActions.ChangeRefused, "Denied")).ShouldBe(2);
        (await AuditCountAsync(registered.Id, WorkgroupAuditActions.ChangeApproved, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task EveryWriteChecksThePermissionAndIsAuditedWhenRefused()
    {
        var registered = await RegisterAsync(NewKey("ops"), ["carol"]);
        var proposed = await ProposeAsync(registered.Id);
        var refusedKey = NewKey("no");
        await using (var scope = Service("outsider"))
        {
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.RegisterAsync(new RegisterWorkgroupRequest(refusedKey, "No", []), Ct))).Refusal.ShouldBe(WorkgroupRefusal.NotAllowed);
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.RenameAsync(registered.Id, registered.ConcurrencyVersion, "No", Ct))).Refusal.ShouldBe(WorkgroupRefusal.NotAllowed);
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.UpdateDraftAsync(registered.Id, registered.Draft!.ConcurrencyVersion, [], Ct))).Refusal.ShouldBe(WorkgroupRefusal.NotAllowed);
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.WithdrawAsync(proposed.Id, null, Ct))).Refusal.ShouldBe(WorkgroupRefusal.NotAllowed);
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.RejectAsync(proposed.Id, "No", Ct))).Refusal.ShouldBe(WorkgroupRefusal.NotAllowed);
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.FindAsync(registered.Key, Ct))).Refusal.ShouldBe(WorkgroupRefusal.NotAllowed);
        }

        await using (var scope = Service("wg-admin-2"))
        {
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.WithdrawAsync(proposed.Id, null, Ct))).Refusal.ShouldBe(WorkgroupRefusal.NotMaker);
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.UpdateDraftAsync(registered.Id, registered.Draft!.ConcurrencyVersion, [], Ct)))
                .Refusal.ShouldBe(WorkgroupRefusal.AlreadyPending, "frozen while pending");
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.RenameAsync(registered.Id, registered.ConcurrencyVersion + 1, "Other", Ct)))
                .Refusal.ShouldBe(WorkgroupRefusal.AlreadyPending);
        }

        (await AuditCountAsync(registered.Id, WorkgroupAuditActions.ChangeRefused, "Denied")).ShouldBe(7, "four outsider writes, and withdraw, edit and rename by another manager");
        var payload = IsSqlServer ? "[PayloadCanonical]" : "payload_canonical";
        var table = IsSqlServer ? "[audit].[AuditEvent]" : "audit.audit_event";
        var action = IsSqlServer ? "[Action]" : "action";
        Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {action} = '{WorkgroupAuditActions.ChangeRefused}' AND {payload} LIKE '%{refusedKey}%'", Ct), CultureInfo.InvariantCulture)
            .ShouldBe(1L, "a refused registration is audited too, by its key");
    }

    [Fact]
    public async Task TheDraftsAuthorCannotApproveAndANewListSupersedesTheOld()
    {
        var both = new FakePermissions(("wg-both", WorkgroupAuditActions.ManageAction), ("wg-both", WorkgroupAuditActions.ApproveAction), ("wg-both", WorkgroupAuditActions.ReadAction), (Author, WorkgroupAuditActions.ManageAction), (Author, WorkgroupAuditActions.ReadAction));
        WorkgroupView registered;
        await using (var scope = Service("wg-both", both))
        {
            registered = await scope.Service.RegisterAsync(new RegisterWorkgroupRequest(NewKey("sec"), "Security", ["dave"]), Ct);
        }

        var proposed = await ProposeAsync(registered.Id);
        await using (var scope = Service("wg-both", both))
        {
            (await Should.ThrowAsync<WorkgroupRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(WorkgroupRefusal.MakerCannotDecide);
        }

        await using (var scope = Service(Checker))
        {
            await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct);
        }

        WorkgroupView started;
        await using (var scope = Service())
        {
            started = await scope.Service.StartNewDraftAsync(registered.Id, ["erin"], Ct);
        }

        started.Draft.ShouldNotBeNull().VersionNumber.ShouldBe(2);
        var second = await ProposeAsync(registered.Id);
        await using (var scope = Service(Checker))
        {
            await scope.Service.ApproveAsync(second.Id, second.ContentHash, null, Ct);
        }

        (await Directory().FindActiveAsync(registered.Key, Ct)).ShouldNotBeNull().Members.ShouldBe(["erin"]);
    }

    [Fact]
    public async Task TheDatabaseRefusesTheSameBySql()
    {
        var registered = await RegisterAsync(NewKey("db"), ["frank"]);
        var draft = registered.Draft!;
        var pending = await ProposeAsync(registered.Id);
        var activate = $"UPDATE {Table("WorkgroupVersion")} SET {Col("State")} = 'Active', {Col("ActivatedByRequestId")} = '{{0}}' WHERE {Col("Id")} = '{draft.VersionId}'";
        foreach (var login in Logins)
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, string.Format(CultureInfo.InvariantCulture, activate, pending.Id), Ct)))
                .Message.ShouldContain("approved request", Case.Insensitive);
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, $"UPDATE {Table("Workgroup")} SET {Col("Key")} = 'renamed_key' WHERE {Col("Id")} = '{registered.Id}'", Ct)))
                .Message.ShouldContain("immutable", Case.Insensitive);
        }

        await using (var scope = Service())
        {
            await scope.Service.WithdrawAsync(pending.Id, null, Ct);
        }

        // A forged approved request whose checker is a member, or with another hash, can't activate the list.
        foreach (var (checker, hash) in new[] { ("frank", draft.ContentHash), ("someone", new string('a', 64)) })
        {
            var forged = Guid.CreateVersion7();
            await db.ExecuteAsync(
                db.OrganizationAppConnectionString,
                $"INSERT INTO {Table("WorkgroupChangeRequest")} ({string.Join(", ", RequestColumns.Select(Col))}) VALUES ('{forged}', '{registered.Id}', '{draft.VersionId}', 1, '{hash}', 'maker', 'r', '2026-11-14T00:00:00+00:00', 'Approved', '{checker}', '2026-11-14T00:00:00+00:00', '2026-11-14T00:00:00+00:00', 't', '2026-11-14T00:00:00+00:00', 't', 1)",
                Ct);
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.OrganizationAppConnectionString, string.Format(CultureInfo.InvariantCulture, activate, forged), Ct)))
                .Message.ShouldMatch("(?i)(member of a workgroup|approved request)", checker);
        }

        // Once active, the list and its members never change; the withdrawn request can't be revived or removed.
        var active = await RegisterAsync(NewKey("db_active"), ["grace"]);
        await ActivateAsync(active.Id);
        foreach (var login in Logins)
        {
            foreach (var sql in new[]
            {
                $"UPDATE {Table("WorkgroupMember")} SET {Col("Person")} = 'mallory' WHERE {Col("VersionId")} = '{active.Draft!.VersionId}'",
                $"DELETE FROM {Table("WorkgroupMember")} WHERE {Col("VersionId")} = '{active.Draft!.VersionId}'",
                $"INSERT INTO {Table("WorkgroupMember")} ({Col("VersionId")}, {Col("Person")}, {Col("CreatedAtUtc")}, {Col("CreatedBy")}, {Col("ModifiedAtUtc")}, {Col("ModifiedBy")}, {Col("Version")}) VALUES ('{active.Draft!.VersionId}', 'mallory', '2026-11-14T00:00:00+00:00', 't', '2026-11-14T00:00:00+00:00', 't', 1)",
                $"DELETE FROM {Table("WorkgroupChangeRequest")} WHERE {Col("Id")} = '{pending.Id}'",
                $"UPDATE {Table("WorkgroupChangeRequest")} SET {Col("State")} = 'Proposed', {Col("Checker")} = NULL, {Col("DecidedAtUtc")} = NULL WHERE {Col("Id")} = '{pending.Id}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct))).Message.ShouldMatch("(?i)(immutable|cannot be deleted)", sql);
            }
        }

        if (!IsSqlServer)
        {
            foreach (var table in new[] { "workgroup_member", "workgroup_version", "workgroup_change_request", "workgroup" })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE organization.{table} CASCADE", Ct)))
                    .Message.ShouldContain("truncated", Case.Insensitive, table);
            }
        }

        (await Directory().FindActiveAsync(active.Key, Ct)).ShouldNotBeNull().Members.ShouldBe(["grace"]);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static readonly string[] RequestColumns =
    [
        "Id", "WorkgroupId", "VersionId", "VersionNumber", "ContentHash", "Maker", "Reason", "ProposedAtUtc",
        "State", "Checker", "DecidedAtUtc", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version",
    ];

    private string[] Logins => [db.OrganizationAppConnectionString, db.MigratorConnectionString];

    private static string NewKey(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private string Table(string name) => IsSqlServer ? $"[organization].[{name}]" : $"organization.{IamModelConventions.ToSnakeCase(name)}";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

    private WorkgroupDirectory Directory() => new(TestSupport.CreateOrganization(db.Provider, db.OrganizationAppConnectionString));

    private async Task<WorkgroupView> RegisterAsync(string key, IReadOnlyList<string?> members)
    {
        await using var scope = Service();
        return await scope.Service.RegisterAsync(new RegisterWorkgroupRequest(key, "Workgroup " + key, members), Ct);
    }

    private async Task<WorkgroupChangeRequestView> ProposeAsync(Guid workgroupId)
    {
        await using var scope = Service();
        return await scope.Service.ProposeAsync(workgroupId, "Ready", Ct);
    }

    private async Task ActivateAsync(Guid workgroupId)
    {
        var proposed = await ProposeAsync(workgroupId);
        await using var scope = Service(Checker);
        await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct);
    }

    private async Task<long> AuditCountAsync(Guid targetId, string action, string outcome)
    {
        var sql = IsSqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [TargetId] = '{targetId}' AND [Action] = '{action}' AND [Outcome] = '{outcome}'"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE target_id = '{targetId}' AND action = '{action}' AND outcome = '{outcome}'";
        return Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }

    private ServiceScope Service(string? actor = Author, FakePermissions? permissions = null)
    {
        var context = TestSupport.CreateOrganization(db.Provider, db.OrganizationAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor), new AuditActionCatalog(WorkgroupAuditActions.All.Select(a => new AuditActionRegistration(a))));
        return new ServiceScope(context, new WorkgroupAdministrationService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions ?? DefaultPermissions));
    }

    private sealed class FakePermissions(params (string Person, string Action)[] grants) : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) => Task.FromResult(grants.Contains((subject, action)));
    }

    private sealed class ServiceScope(OrganizationDbContext context, WorkgroupAdministrationService service) : IAsyncDisposable
    {
        public WorkgroupAdministrationService Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
