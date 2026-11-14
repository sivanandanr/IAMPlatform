using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Application.Applications;
using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 3 application catalog behaviour that must be identical on SQL Server and PostgreSQL (DL-066, AR-2.1 to AR-2.4,
/// IAM-FR-010, spec 2.2): second-person activation, the owner never decides, empty required roles block activation, role
/// types need a second person, every refusal is audited, and the database refuses the same things by SQL.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class ApplicationCatalogParityTests(ParityDatabase db)
{
    private const string Author = "app-admin";
    private const string OtherAuthor = "app-admin-2";
    private const string Checker = "app-checker";
    private const string OwnerChecker = "owner-who-checks";

    private static readonly FakePermissions DefaultPermissions = new(
        (Author, CatalogAuditActions.ManageAction),
        (Author, CatalogAuditActions.ReadAction),
        (OtherAuthor, CatalogAuditActions.ManageAction),
        (OtherAuthor, CatalogAuditActions.ReadAction),
        (Checker, CatalogAuditActions.ApproveAction),
        (Checker, CatalogAuditActions.ReadAction),
        (OwnerChecker, CatalogAuditActions.ApproveAction),
        (OwnerChecker, CatalogAuditActions.ReadAction));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    // ---- Schema -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMigrationAppliesOnceAndSeedsTheBuiltInRoleTypes()
    {
        db.CatalogFirstRunApplied.Count.ShouldBe(2);
        db.CatalogFirstRunApplied[0].ShouldEndWith("_ApplicationCatalog");
        db.CatalogFirstRunApplied[1].ShouldEndWith("_ApplicationCatalogHardening");
        db.CatalogSecondRunPending.ShouldBeEmpty();

        var tables = IsSqlServer
            ? "SELECT STRING_AGG(TABLE_NAME, ',') FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'catalog'"
            : "SELECT string_agg(table_name, ',') FROM information_schema.tables WHERE table_schema = 'catalog'";
        string[] expected = IsSqlServer
            ? ["Application", "ApplicationRoleType", "ApplicationSettingsRequest", "ApplicationSettingsRole", "ApplicationSettingsVersion", "__EFMigrationsHistory"]
            : ["__ef_migrations_history", "application", "application_role_type", "application_settings_request", "application_settings_role", "application_settings_version"];
        ((string)(await db.ScalarAsync(db.MigratorConnectionString, tables, Ct))!).Split(',').Order(StringComparer.Ordinal).ShouldBe(expected.Order(StringComparer.Ordinal).ToArray());

        await using var scope = Service();
        var builtIns = (await scope.Service.ListRoleTypesAsync(Ct)).Where(t => t.State == "BuiltIn").Select(t => (t.Id, t.Code, t.DisplayName)).ToList();
        builtIns.OrderBy(t => t.Code, StringComparer.Ordinal).ShouldBe(BuiltInRoleTypes.All.OrderBy(t => t.Code, StringComparer.Ordinal));
    }

    // ---- Activation with a second person ------------------------------------------------------------------------

    [Fact]
    public async Task AnApplicationIsActivatedByASecondPersonAndAudited()
    {
        var registered = await RegisterAsync(NewKey("erp"));
        registered.IsActive.ShouldBeFalse();
        registered.Draft.ShouldNotBeNull().Roles.ShouldHaveSingleItem().ShouldBe(new ApplicationRoleView("it_admin", "Required", "Group", "grp-erp-it"));

        var proposed = await ProposeAsync(registered.Id);
        proposed.ProposedOwner.ShouldBe("owner-1");
        proposed.ContentHash.ShouldBe(registered.Draft!.ContentHash);

        await using (var scope = Service(Checker))
        {
            var review = (await scope.Service.ReviewRequestAsync(proposed.Id, Ct)).ShouldNotBeNull();
            review.Draft.ShouldNotBeNull().ContentHash.ShouldBe(proposed.ContentHash);
            (await scope.Service.ApproveAsync(proposed.Id, review.Draft.ContentHash, "Checked", Ct)).State.ShouldBe("Approved");
            (await scope.Service.ReviewRequestAsync(proposed.Id, Ct)).ShouldNotBeNull().Draft.ShouldBeNull("nothing is a draft any more");
        }

        await using (var scope = Service())
        {
            var found = (await scope.Service.FindAsync(registered.Key, Ct)).ShouldNotBeNull();
            found.IsActive.ShouldBeTrue();
            found.Draft.ShouldBeNull();
            var active = found.Active.ShouldNotBeNull();
            active.State.ShouldBe("Active");
            active.Owner.ShouldBe("owner-1");
            active.ContentHash.ShouldBe(proposed.ContentHash);
        }

        (await AuditCountAsync(registered.Id, CatalogAuditActions.Registered, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(registered.Id, CatalogAuditActions.ChangeProposed, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(registered.Id, CatalogAuditActions.ChangeApproved, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task AnEmptyRequiredRoleBlocksActivation()
    {
        var registered = await RegisterAsync(NewKey("crm"), Settings(holderKind: null, holderId: null));
        await using (var scope = Service())
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeAsync(registered.Id, "Ready", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.RequiredRoleEmpty);
        }

        (await AuditCountAsync(registered.Id, CatalogAuditActions.ChangeRefused, "Denied")).ShouldBe(1);

        // Filling the role makes it proposable.
        await using (var scope = Service())
        {
            await scope.Service.UpdateDraftAsync(registered.Id, registered.Draft!.ConcurrencyVersion, Settings(), Ct);
            (await scope.Service.ProposeAsync(registered.Id, "Ready", Ct)).State.ShouldBe("Proposed");
        }
    }

    [Fact]
    public async Task OnlyAnAllowedSecondPersonWhoIsNotTheOwnerOrAnAuthorCanApprove()
    {
        var registered = await RegisterAsync(NewKey("hr"), Settings(owner: OwnerChecker));
        var proposed = await ProposeAsync(registered.Id);

        async Task Refused(string actor, ApplicationRefusal refusal, string? hash = null, FakePermissions? permissions = null)
        {
            await using var scope = Service(actor, permissions);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, hash ?? proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(refusal, actor);
        }

        await Refused(Author, ApplicationRefusal.MakerCannotDecide);
        await Refused(OwnerChecker, ApplicationRefusal.OwnerCannotDecide);
        await Refused("outsider", ApplicationRefusal.NotAllowed);
        await Refused(Checker, ApplicationRefusal.DraftChanged, hash: new string('0', 64));
        await Refused(Checker, ApplicationRefusal.MakerNoLongerAllowed, permissions: new FakePermissions((Checker, CatalogAuditActions.ApproveAction)));

        // Someone who wrote the draft can't approve it, even holding the approve permission now.
        var both = new FakePermissions(
            (OtherAuthor, CatalogAuditActions.ManageAction), (OtherAuthor, CatalogAuditActions.ApproveAction), (OtherAuthor, CatalogAuditActions.ReadAction),
            (Author, CatalogAuditActions.ManageAction), (Author, CatalogAuditActions.ReadAction));
        var edited = await RegisterAsync(NewKey("hr2"), as_: OtherAuthor);
        var second = await ProposeAsync(edited.Id);
        await using (var scope = Service(OtherAuthor, both))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(second.Id, second.ContentHash, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.MakerCannotDecide, "the draft's creator");
        }

        // The last editor too: created by the author, edited by someone who now holds the approve permission.
        var lastEdited = await RegisterAsync(NewKey("hr3"));
        await using (var scope = Service(OtherAuthor, both))
        {
            await scope.Service.UpdateDraftAsync(lastEdited.Id, lastEdited.Draft!.ConcurrencyVersion, Settings(owner: "owner-9"), Ct);
        }

        var third = await ProposeAsync(lastEdited.Id);
        await using (var scope = Service(OtherAuthor, both))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(third.Id, third.ContentHash, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.MakerCannotDecide, "the draft's last editor");
        }

        (await AuditCountAsync(registered.Id, CatalogAuditActions.ChangeRefused, "Denied")).ShouldBe(5);
        await using (var scope = Service(Checker))
        {
            (await scope.Service.ReviewRequestAsync(proposed.Id, Ct)).ShouldNotBeNull().Request.State.ShouldBe("Proposed");
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RejectAsync(proposed.Id, " ", Ct))).Refusal.ShouldBe(ApplicationRefusal.InvalidReason);
            (await scope.Service.RejectAsync(proposed.Id, "Wrong owner", Ct)).State.ShouldBe("Rejected");
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotProposed);
        }
    }

    /// <summary>DL-067a: a checker named as a person holding a role in the settings can't approve or reject them.</summary>
    [Fact]
    public async Task APersonNamedAsARoleHolderCannotDecide()
    {
        var settings = new ApplicationSettingsInput("owner-1",
        [
            new ApplicationRoleInput("it_admin", RoleRequirement.Required, RoleHolderKind.Group, "grp-1"),
            new ApplicationRoleInput("user_admin", RoleRequirement.Optional, RoleHolderKind.Person, Checker),
        ]);
        var registered = await RegisterAsync(NewKey("holder"), settings);
        var proposed = await ProposeAsync(registered.Id);
        await using (var scope = Service(Checker))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.HolderCannotDecide);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RejectAsync(proposed.Id, "Mine", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.HolderCannotDecide);
        }

        (await AuditCountAsync(registered.Id, CatalogAuditActions.ChangeRefused, "Denied")).ShouldBe(2);
        var payload = IsSqlServer ? "[PayloadCanonical]" : "payload_canonical";
        var table = IsSqlServer ? "[audit].[AuditEvent]" : "audit.audit_event";
        var action = IsSqlServer ? "[Action]" : "action";
        Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {action} = '{CatalogAuditActions.ChangeRefused}' AND {payload} LIKE '%{proposed.Id}%'", Ct), CultureInfo.InvariantCulture)
            .ShouldBe(2L, "each refused decision names the request it refused");
        await using (var scope = Service(OwnerChecker))
        {
            (await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)).State.ShouldBe("Approved");
        }
    }

    /// <summary>
    /// Review finding: an edit and a proposal both bump the application row, so an edit working from a copy read before a
    /// proposal fails instead of changing a draft under it. The stale copy is held by a scope that loaded it first.
    /// </summary>
    [Fact]
    public async Task AnEditRacingAProposalLoses()
    {
        var registered = await RegisterAsync(NewKey("race"));
        await using (var stale = Service())
        {
            await stale.LoadApplicationAsync(registered.Id);
            var proposed = await ProposeAsync(registered.Id);
            await using (var scope = Service())
            {
                await scope.Service.WithdrawAsync(proposed.Id, null, Ct);
            }

            await Should.ThrowAsync<ApplicationConflictException>(() =>
                stale.Service.UpdateDraftAsync(registered.Id, registered.Draft!.ConcurrencyVersion, Settings(owner: "owner-2"), Ct));
        }

        // The draft's authors are untouched by the proposal: it still names its creator, not the proposer's bump.
        await using (var scope = Service())
        {
            var found = (await scope.Service.FindAsync(registered.Key, Ct)).ShouldNotBeNull();
            found.ConcurrencyVersion.ShouldBeGreaterThan(registered.ConcurrencyVersion, "the proposal bumped the application row");
            found.Draft.ShouldNotBeNull().ConcurrencyVersion.ShouldBe(registered.Draft!.ConcurrencyVersion, "the draft row is untouched");
        }

        // A draft changed under a pending request (only possible by SQL) is not shown as what the request proposes.
        var second = await ProposeAsync(registered.Id);
        await db.ExecuteAsync(
            db.CatalogAppConnectionString,
            $"UPDATE {Table("ApplicationSettingsVersion")} SET {Col("ContentHash")} = '{new string('b', 64)}' WHERE {Col("Id")} = '{registered.Draft!.VersionId}'",
            Ct);
        await using (var scope = Service(Checker))
        {
            (await scope.Service.ReviewRequestAsync(second.Id, Ct)).ShouldNotBeNull().Draft.ShouldBeNull();
        }
    }

    [Fact]
    public async Task ChangingTheOwnerOfAnActiveApplicationNeedsASecondPerson()
    {
        var registered = await RegisterAsync(NewKey("mail"));
        await ActivateAsync(registered.Id);

        ApplicationView started;
        await using (var scope = Service())
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.UpdateDraftAsync(registered.Id, 0, Settings(owner: "owner-2"), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NoDraft);
            started = await scope.Service.StartNewDraftAsync(registered.Id, Settings(owner: "owner-2"), Ct);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.StartNewDraftAsync(registered.Id, Settings(), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.DraftExists);
        }

        started.Active.ShouldNotBeNull().Owner.ShouldBe("owner-1");
        started.Draft.ShouldNotBeNull().VersionNumber.ShouldBe(2);
        (await AuditCountAsync(registered.Id, CatalogAuditActions.ChangeRefused, "Denied")).ShouldBe(2, "the refused edit (no draft) and the refused new draft");

        var proposed = await ProposeAsync(registered.Id);
        await using (var scope = Service())
        {
            // The draft is frozen while the request is pending.
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.UpdateDraftAsync(registered.Id, started.Draft.ConcurrencyVersion, Settings(owner: "owner-3"), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.AlreadyPending);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeAsync(registered.Id, "Again", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.AlreadyPending);
        }

        await using (var scope = Service(Checker))
        {
            await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct);
        }

        await using (var scope = Service())
        {
            var found = (await scope.Service.FindAsync(registered.Key, Ct)).ShouldNotBeNull();
            found.Active.ShouldNotBeNull().Owner.ShouldBe("owner-2");
            found.Active.VersionNumber.ShouldBe(2);
        }

        var states = IsSqlServer
            ? $"SELECT STRING_AGG([State], ',') WITHIN GROUP (ORDER BY [VersionNumber]) FROM [catalog].[ApplicationSettingsVersion] WHERE [ApplicationId] = '{registered.Id}'"
            : $"SELECT string_agg(state, ',' ORDER BY version_number) FROM catalog.application_settings_version WHERE application_id = '{registered.Id}'";
        (await db.ScalarAsync(db.CatalogAppConnectionString, states, Ct)).ShouldBe("Superseded,Active");
    }

    [Fact]
    public async Task EveryWriteChecksThePermissionAndOnlyTheMakerWithdraws()
    {
        var registered = await RegisterAsync(NewKey("wiki"));
        var proposed = await ProposeAsync(registered.Id);
        var refusedKey = NewKey("no");
        await using (var scope = Service("outsider"))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RegisterAsync(new RegisterApplicationRequest(refusedKey, "No", Settings()), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RenameAsync(registered.Id, registered.ConcurrencyVersion, "No", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.UpdateDraftAsync(registered.Id, registered.Draft!.ConcurrencyVersion, Settings(), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.StartNewDraftAsync(registered.Id, Settings(), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.WithdrawAsync(proposed.Id, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RejectAsync(proposed.Id, "No", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeRoleTypeAsync(NewKey("rt"), "No", "No", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.FindAsync(registered.Key, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed, "reads check the read permission too");
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ReviewRequestAsync(proposed.Id, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ListRoleTypesAsync(Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotAllowed);
        }

        await using (var scope = Service(OtherAuthor))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.WithdrawAsync(proposed.Id, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotMaker);
        }

        await using (var scope = Service())
        {
            (await scope.Service.WithdrawAsync(proposed.Id, "Not yet", Ct)).State.ShouldBe("Withdrawn");
        }

        (await AuditCountAsync(registered.Id, CatalogAuditActions.ChangeWithdrawn, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(registered.Id, CatalogAuditActions.ChangeRefused, "Denied")).ShouldBe(6, "rename, edit, new draft, withdraw and reject by the outsider, and withdraw by another manager");
        var payload = IsSqlServer ? "[PayloadCanonical]" : "payload_canonical";
        var table = IsSqlServer ? "[audit].[AuditEvent]" : "audit.audit_event";
        var action = IsSqlServer ? "[Action]" : "action";
        Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, $"SELECT COUNT(*) FROM {table} WHERE {action} = '{CatalogAuditActions.ChangeRefused}' AND {payload} LIKE '%{refusedKey}%'", Ct), CultureInfo.InvariantCulture)
            .ShouldBe(1L, "a refused registration is audited too, by its key");
    }

    [Fact]
    public async Task ARenameNeedsNoSecondPersonButIsAuditedAndChecksConcurrency()
    {
        var registered = await RegisterAsync(NewKey("portal"));
        await using var scope = Service();
        var renamed = await scope.Service.RenameAsync(registered.Id, registered.ConcurrencyVersion, "Customer portal", Ct);
        renamed.DisplayName.ShouldBe("Customer portal");
        renamed.Draft.ShouldNotBeNull().ContentHash.ShouldBe(registered.Draft!.ContentHash, "a name is not access");
        await using var stale = Service();
        await Should.ThrowAsync<ApplicationConflictException>(() => stale.Service.RenameAsync(registered.Id, registered.ConcurrencyVersion, "Other", Ct));
        (await AuditCountAsync(registered.Id, CatalogAuditActions.Renamed, "Succeeded")).ShouldBe(1);

        // Frozen while a change is pending, so the reviewer approves the application they saw (review finding).
        await ProposeAsync(registered.Id);
        await using var pending = Service();
        (await Should.ThrowAsync<ApplicationRefusedException>(() => pending.Service.RenameAsync(registered.Id, renamed.ConcurrencyVersion + 1, "Sandbox", Ct)))
            .Refusal.ShouldBe(ApplicationRefusal.AlreadyPending);
    }

    // ---- Role types ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeploymentRoleTypeIsUsableOnlyAfterASecondPersonApprovesIt()
    {
        var code = NewKey("security_contact");
        ApplicationRoleTypeView proposed;
        await using (var scope = Service())
        {
            proposed = await scope.Service.ProposeRoleTypeAsync(code, "Security contact", "Audit asked for it", Ct);
            proposed.State.ShouldBe("Proposed");
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeRoleTypeAsync(code, "Again", "Again", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.RoleTypeExists);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeRoleTypeAsync("it_admin", "IT", "Clash", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.RoleTypeExists);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RegisterAsync(new RegisterApplicationRequest(NewKey("sec"), "Sec", WithRole(code)), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.RoleTypeNotUsable);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveRoleTypeAsync(proposed.Id, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.MakerCannotDecide);
        }

        await using (var scope = Service(Checker))
        {
            (await scope.Service.ApproveRoleTypeAsync(proposed.Id, "Agreed", Ct)).State.ShouldBe("Approved");
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RejectRoleTypeAsync(proposed.Id, "Late", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotProposed);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RejectRoleTypeAsync(BuiltInRoleTypes.ItAdmin, "Remove", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotProposed);
        }

        await using (var scope = Service())
        {
            var registered = await scope.Service.RegisterAsync(new RegisterApplicationRequest(NewKey("sec"), "Sec", WithRole(code)), Ct);
            registered.Draft.ShouldNotBeNull().Roles.Select(r => r.RoleTypeCode).Order(StringComparer.Ordinal).ShouldBe(new[] { "it_admin", code }.Order(StringComparer.Ordinal));
            (await Should.ThrowAsync<ApplicationRejectedException>(() => scope.Service.RegisterAsync(new RegisterApplicationRequest(NewKey("sec"), "Sec", WithRole("no_such_type")), Ct)))
                .Field.ShouldBe("RoleTypeCode");
        }

        // A withdrawn type frees its code.
        var other = NewKey("vendor_contact");
        await using (var scope = Service())
        {
            var p = await scope.Service.ProposeRoleTypeAsync(other, "Vendor contact", "Maybe", Ct);
            (await scope.Service.WithdrawRoleTypeAsync(p.Id, null, Ct)).State.ShouldBe("Withdrawn");
            (await scope.Service.ProposeRoleTypeAsync(other, "Vendor contact", "Now", Ct)).State.ShouldBe("Proposed");
        }

        (await AuditCountAsync(proposed.Id, CatalogAuditActions.RoleTypeApproved, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(proposed.Id, CatalogAuditActions.RoleTypeRefused, "Denied")).ShouldBe(2, "self-approval and late reject");
    }

    // ---- The database refuses the same things by SQL ------------------------------------------------------------

    [Fact]
    public async Task ApplicationsAndRoleTypesCannotBeRemovedOrForgedBySql()
    {
        var registered = await RegisterAsync(NewKey("ldap"));
        foreach (var login in Logins)
        {
            foreach (var sql in new[]
            {
                $"DELETE FROM {Table("Application")} WHERE {Col("Id")} = '{registered.Id}'",
                $"UPDATE {Table("Application")} SET {Col("Key")} = 'renamed_key' WHERE {Col("Id")} = '{registered.Id}'",
                $"DELETE FROM {Table("ApplicationRoleType")} WHERE {Col("Id")} = '{BuiltInRoleTypes.ItAdmin}'",
                $"UPDATE {Table("ApplicationRoleType")} SET {Col("DisplayName")} = 'Changed' WHERE {Col("Id")} = '{BuiltInRoleTypes.ItAdmin}'",
                $"UPDATE {Table("ApplicationRoleType")} SET {Col("State")} = 'Withdrawn' WHERE {Col("Id")} = '{BuiltInRoleTypes.ItAdmin}'",
                RoleTypeInsert("BuiltIn", null),
                RoleTypeInsert("Approved", "checker"),
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct))).Message
                    .ShouldMatch("(?i)(cannot be deleted|immutable|second person|check|constraint)", sql);
            }
        }
    }

    /// <summary>
    /// Review finding M2: on SQL Server foreign keys are checked before AFTER triggers, so rows something references are
    /// refused by the key, not the trigger. These rows are referenced by nothing, so the trigger itself refuses them.
    /// </summary>
    [Fact]
    public async Task UnreferencedRowsAreRefusedByTheTriggersThemselves()
    {
        ApplicationRoleTypeView type;
        await using (var scope = Service())
        {
            type = await scope.Service.ProposeRoleTypeAsync(NewKey("lonely"), "Nobody uses this", "Test", Ct);
        }

        var registered = await RegisterAsync(NewKey("lonely_app"));
        var withdrawn = await ProposeAsync(registered.Id);
        await using (var scope = Service())
        {
            await scope.Service.WithdrawAsync(withdrawn.Id, null, Ct);
        }

        foreach (var login in Logins)
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, $"DELETE FROM {Table("ApplicationRoleType")} WHERE {Col("Id")} = '{type.Id}'", Ct)))
                .Message.ShouldContain("cannot be deleted", Case.Insensitive);
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, $"DELETE FROM {Table("ApplicationSettingsRequest")} WHERE {Col("Id")} = '{withdrawn.Id}'", Ct)))
                .Message.ShouldContain("cannot be deleted", Case.Insensitive);
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, $"UPDATE {Table("ApplicationSettingsRequest")} SET {Col("State")} = 'Proposed', {Col("Checker")} = NULL, {Col("DecidedAtUtc")} = NULL WHERE {Col("Id")} = '{withdrawn.Id}'", Ct)))
                .Message.ShouldContain("immutable", Case.Insensitive);
        }

        // PostgreSQL also refuses TRUNCATE of settings versions and their roles (SQL Server TRUNCATE fires no trigger: R-034).
        if (!IsSqlServer)
        {
            foreach (var table in new[] { "application_settings_role", "application_settings_version", "application_settings_request", "application" })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE catalog.{table} CASCADE", Ct)))
                    .Message.ShouldMatch("(?i)(truncate|cannot be deleted|immutable)", table);
            }
        }
    }

    /// <summary>DL-067a in the database: a forged approved request decided by a person holder can't activate the settings.</summary>
    [Fact]
    public async Task ARoleHolderWhoApprovedCannotActivateBySql()
    {
        var settings = new ApplicationSettingsInput("owner-1",
        [
            new ApplicationRoleInput("it_admin", RoleRequirement.Required, RoleHolderKind.Person, "p-holder"),
        ]);
        var registered = await RegisterAsync(NewKey("holder_sql"), settings);
        var draft = registered.Draft!;
        var forged = await ForgeApprovedRequestAsync(registered.Id, draft.VersionId, 1, draft.ContentHash, "owner-1", checker: "p-holder");
        (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
            db.CatalogAppConnectionString,
            $"UPDATE {Table("ApplicationSettingsVersion")} SET {Col("State")} = 'Active', {Col("ActivatedByRequestId")} = '{forged}' WHERE {Col("Id")} = '{draft.VersionId}'",
            Ct)))
            .Message.ShouldContain("role holder", Case.Insensitive);
    }

    [Fact]
    public async Task SettingsBecomeActiveOnlyThroughAnApprovedRequestNamingThem()
    {
        var registered = await RegisterAsync(NewKey("sap"));
        var draft = registered.Draft!;
        var pending = await ProposeAsync(registered.Id);
        var activate = $"UPDATE {Table("ApplicationSettingsVersion")} SET {Col("State")} = 'Active', {Col("ActivatedByRequestId")} = '{{0}}' WHERE {Col("Id")} = '{draft.VersionId}'";
        foreach (var login in Logins)
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, $"UPDATE {Table("ApplicationSettingsVersion")} SET {Col("State")} = 'Active' WHERE {Col("Id")} = '{draft.VersionId}'", Ct)))
                .Message.ShouldMatch("(?i)(check|constraint|approved request)");
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, string.Format(CultureInfo.InvariantCulture, activate, pending.Id), Ct)))
                .Message.ShouldContain("approved request", Case.Insensitive);
        }

        // Forged approved requests (the app login can write them, R-033) that differ from the draft can't activate it.
        await using (var scope = Service())
        {
            await scope.Service.WithdrawAsync(pending.Id, null, Ct);
        }

        foreach (var (hash, number, owner) in new[] { (new string('a', 64), 1, "owner-1"), (draft.ContentHash, 2, "owner-1"), (draft.ContentHash, 1, "someone-else") })
        {
            var forged = await ForgeApprovedRequestAsync(registered.Id, draft.VersionId, number, hash, owner);
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.CatalogAppConnectionString, string.Format(CultureInfo.InvariantCulture, activate, forged), Ct)))
                .Message.ShouldContain("approved request", Case.Insensitive, $"{hash[..6]} {number} {owner}");
        }

        // A matching forged request, but a required role emptied by SQL, is still refused (AR-2.3).
        await db.ExecuteAsync(db.CatalogAppConnectionString, $"UPDATE {Table("ApplicationSettingsRole")} SET {Col("HolderKind")} = NULL, {Col("HolderId")} = NULL WHERE {Col("SettingsVersionId")} = '{draft.VersionId}'", Ct);
        var matching = await ForgeApprovedRequestAsync(registered.Id, draft.VersionId, 1, draft.ContentHash, "owner-1");
        (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.CatalogAppConnectionString, string.Format(CultureInfo.InvariantCulture, activate, matching), Ct)))
            .Message.ShouldContain("empty required role", Case.Insensitive);

        // Requests a second person never decided, or that the owner decided, can't even be written.
        foreach (var (checker, owner) in new[] { ("maker", "owner-1"), ("owner-1", "owner-1") })
        {
            (await Should.ThrowAsync<Exception>(() => ForgeApprovedRequestAsync(registered.Id, draft.VersionId, 1, draft.ContentHash, owner, checker)))
                .Message.ShouldMatch("(?i)(check|constraint)");
        }
    }

    [Fact]
    public async Task ActiveSettingsTheirRolesAndDecidedRequestsNeverChange()
    {
        var registered = await RegisterAsync(NewKey("ad"));
        var requestId = await ActivateAsync(registered.Id);
        var versionId = registered.Draft!.VersionId;
        foreach (var login in Logins)
        {
            foreach (var sql in new[]
            {
                $"UPDATE {Table("ApplicationSettingsVersion")} SET {Col("Owner")} = 'someone-else' WHERE {Col("Id")} = '{versionId}'",
                $"UPDATE {Table("ApplicationSettingsVersion")} SET {Col("State")} = 'Draft', {Col("ActivatedByRequestId")} = NULL WHERE {Col("Id")} = '{versionId}'",
                $"DELETE FROM {Table("ApplicationSettingsVersion")} WHERE {Col("Id")} = '{versionId}'",
                $"UPDATE {Table("ApplicationSettingsRole")} SET {Col("HolderId")} = 'grp-attacker' WHERE {Col("SettingsVersionId")} = '{versionId}'",
                $"DELETE FROM {Table("ApplicationSettingsRole")} WHERE {Col("SettingsVersionId")} = '{versionId}'",
                $"INSERT INTO {Table("ApplicationSettingsRole")} ({string.Join(", ", RoleColumns.Select(Col))}) VALUES ('{versionId}', '{BuiltInRoleTypes.UserAdmin}', 'Optional', 'Person', 'p-attacker', '2026-11-14T00:00:00+00:00', 't', '2026-11-14T00:00:00+00:00', 't', 1)",
                $"UPDATE {Table("ApplicationSettingsRequest")} SET {Col("State")} = 'Proposed', {Col("Checker")} = NULL, {Col("DecidedAtUtc")} = NULL WHERE {Col("Id")} = '{requestId}'",
                $"DELETE FROM {Table("ApplicationSettingsRequest")} WHERE {Col("Id")} = '{requestId}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct))).Message
                    .ShouldMatch("(?i)(immutable|cannot be deleted|check|constraint)", sql);
            }
        }

        await using var scope = Service();
        (await scope.Service.FindAsync(registered.Key, Ct)).ShouldNotBeNull().Active.ShouldNotBeNull().Roles.ShouldHaveSingleItem().HolderId.ShouldBe("grp-erp-it");
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static readonly string[] RoleColumns =
        ["SettingsVersionId", "RoleTypeId", "Requirement", "HolderKind", "HolderId", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version"];

    private static readonly string[] RequestColumns =
    [
        "Id", "ApplicationId", "SettingsVersionId", "VersionNumber", "ContentHash", "ProposedOwner", "Maker", "Reason", "ProposedAtUtc",
        "State", "Checker", "DecidedAtUtc", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version",
    ];

    private string[] Logins => [db.CatalogAppConnectionString, db.MigratorConnectionString];

    private static ApplicationSettingsInput Settings(string owner = "owner-1", RoleHolderKind? holderKind = RoleHolderKind.Group, string? holderId = "grp-erp-it") =>
        new(owner, [new ApplicationRoleInput("it_admin", RoleRequirement.Required, holderKind, holderId)]);

    private static ApplicationSettingsInput WithRole(string code) =>
        new("owner-1", [new ApplicationRoleInput("it_admin", RoleRequirement.Required, RoleHolderKind.Group, "grp-1"), new ApplicationRoleInput(code, RoleRequirement.Optional, null, null)]);

    private static string NewKey(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private string Table(string name) => IsSqlServer ? $"[catalog].[{name}]" : $"catalog.{IamModelConventions.ToSnakeCase(name)}";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

    private string RoleTypeInsert(string state, string? checker) =>
        $"INSERT INTO {Table("ApplicationRoleType")} ({Col("Id")}, {Col("Code")}, {Col("DisplayName")}, {Col("State")}, {Col("Maker")}, {Col("Reason")}, {Col("ProposedAtUtc")}, {Col("Checker")}, {Col("DecidedAtUtc")}, {Col("CreatedAtUtc")}, {Col("CreatedBy")}, {Col("ModifiedAtUtc")}, {Col("ModifiedBy")}, {Col("Version")}) "
        + (checker is null
            ? $"VALUES ('{Guid.CreateVersion7()}', '{NewKey("forged")}', 'Forged', '{state}', NULL, NULL, NULL, NULL, NULL, '2026-11-14T00:00:00+00:00', 't', '2026-11-14T00:00:00+00:00', 't', 1)"
            : $"VALUES ('{Guid.CreateVersion7()}', '{NewKey("forged")}', 'Forged', '{state}', 'maker', 'r', '2026-11-14T00:00:00+00:00', '{checker}', '2026-11-14T00:00:00+00:00', '2026-11-14T00:00:00+00:00', 't', '2026-11-14T00:00:00+00:00', 't', 1)");

    private async Task<Guid> ForgeApprovedRequestAsync(Guid applicationId, Guid versionId, int number, string hash, string owner, string checker = "checker")
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            db.CatalogAppConnectionString,
            $"INSERT INTO {Table("ApplicationSettingsRequest")} ({string.Join(", ", RequestColumns.Select(Col))}) "
            + $"VALUES ('{id}', '{applicationId}', '{versionId}', {number}, '{hash}', '{owner}', 'maker', 'r', '2026-11-14T00:00:00+00:00', 'Approved', '{checker}', '2026-11-14T00:00:00+00:00', '2026-11-14T00:00:00+00:00', 't', '2026-11-14T00:00:00+00:00', 't', 1)",
            Ct);
        return id;
    }

    private async Task<ApplicationView> RegisterAsync(string key, ApplicationSettingsInput? settings = null, string as_ = Author)
    {
        await using var scope = Service(as_);
        return await scope.Service.RegisterAsync(new RegisterApplicationRequest(key, "Application " + key, settings ?? Settings()), Ct);
    }

    private async Task<ApplicationSettingsRequestView> ProposeAsync(Guid applicationId)
    {
        await using var scope = Service();
        return await scope.Service.ProposeAsync(applicationId, "Ready for use", Ct);
    }

    private async Task<Guid> ActivateAsync(Guid applicationId)
    {
        var proposed = await ProposeAsync(applicationId);
        await using var scope = Service(Checker);
        await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct);
        return proposed.Id;
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
        var context = TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider),
            TimeProvider.System,
            new FixedActor(actor),
            new AuditActionCatalog(CatalogAuditActions.All.Select(a => new AuditActionRegistration(a))));
        return new ServiceScope(context, new ApplicationCatalogAdministrationService(
            context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions ?? DefaultPermissions));
    }

    private sealed class FakePermissions(params (string Person, string Action)[] grants) : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) =>
            Task.FromResult(grants.Contains((subject, action)));
    }

    private sealed class ServiceScope(CatalogDbContext context, ApplicationCatalogAdministrationService service) : IAsyncDisposable
    {
        public ApplicationCatalogAdministrationService Service { get; } = service;

        /// <summary>Loads and tracks the application now, so a later call in this scope works from this (possibly stale) copy.</summary>
        public Task<IAM.Modules.Catalog.Domain.Applications.Application> LoadApplicationAsync(Guid id) => context.Applications
            .Include(a => a.Versions).ThenInclude(v => v.Roles)
            .SingleAsync(a => a.Id == id, TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
