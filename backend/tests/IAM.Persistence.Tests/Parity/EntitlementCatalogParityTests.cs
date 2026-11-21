using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Application.Applications;
using IAM.Modules.Catalog.Application.Entitlements;
using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Catalog.Infrastructure.Entitlements;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using IAM.Modules.Organization.Contracts;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 8 slice 1 entitlement catalog behaviour that must be identical on SQL Server and PostgreSQL (DL-084h, DL-087, spec
/// v3.2 §2.5, §3.1, §6.3, §6.4): a new owner accepts, then a second person who neither wrote nor accepted the draft activates
/// it; every refusal is audited; and the database refuses the same things by SQL.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class EntitlementCatalogParityTests(ParityDatabase db)
{
    private const string Author = "ent-admin";
    private const string OtherAuthor = "ent-admin-2";
    private const string Checker = "ent-checker";
    private const string Owner = "ent-owner";
    private const string Member = "ent-member";
    private const string OwnerGroup = "grp_ent_owners";

    private static readonly FakePermissions DefaultPermissions = new(
        (Author, EntitlementAuditActions.ManageAction),
        (Author, EntitlementAuditActions.ReadAction),
        (Author, EntitlementAuditActions.AcceptAction),
        (Author, CatalogAuditActions.ManageAction),
        (OtherAuthor, EntitlementAuditActions.ManageAction),
        (OtherAuthor, EntitlementAuditActions.ReadAction),
        (OtherAuthor, EntitlementAuditActions.AcceptAction),
        (Checker, EntitlementAuditActions.ApproveAction),
        (Checker, EntitlementAuditActions.ReadAction),
        (Checker, EntitlementAuditActions.AcceptAction),
        (Checker, CatalogAuditActions.ApproveAction),
        (Owner, EntitlementAuditActions.AcceptAction),
        (Member, EntitlementAuditActions.AcceptAction),
        (Member, EntitlementAuditActions.ApproveAction));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task AnEntitlementIsAcceptedByItsOwnerThenActivatedByASecondPersonAndAudited()
    {
        var app = await ActiveApplicationAsync();
        var registered = await RegisterAsync(app.Id, NewKey("readers"));
        registered.IsActive.ShouldBeFalse();
        registered.DraftNeedsAcceptance.ShouldBeTrue();
        var draft = registered.Draft.ShouldNotBeNull();
        draft.AllowedAccountClasses.ShouldBe(["Primary", "Service"]);
        draft.AccessFor.ShouldBe(new AccessForView("RequesterChooses", null, 30, 90));

        await using (var scope = Service())
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeAsync(registered.Id, "Ready", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.OwnerNotAccepted);
        }

        await using (var scope = Service(Owner))
        {
            var accepted = await scope.Service.AcceptOwnershipAsync(registered.Id, draft.ContentHash, Ct);
            accepted.Draft.ShouldNotBeNull().OwnerAcceptedBy.ShouldBe(Owner);
        }

        var proposed = await ProposeAsync(registered.Id);
        proposed.AcceptedBy.ShouldBe(Owner);
        await using (var scope = Service(Checker))
        {
            var review = (await scope.Service.ReviewRequestAsync(proposed.Id, Ct)).ShouldNotBeNull();
            review.Draft.ShouldNotBeNull().ContentHash.ShouldBe(proposed.ContentHash);
            (await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, "Checked", Ct)).State.ShouldBe("Approved");
        }

        await using (var scope = Service())
        {
            var found = (await scope.Service.FindAsync(app.Key, registered.Key, Ct)).ShouldNotBeNull();
            found.IsActive.ShouldBeTrue();
            found.Draft.ShouldBeNull();
            found.Active.ShouldNotBeNull().OwnerAcceptedBy.ShouldBe(Owner);
            (await scope.Service.ListAsync(app.Key, Ct)).ShouldNotBeNull().ShouldContain(e => e.Key == registered.Key && e.IsActive && !e.HasDraft);
        }

        (await AuditCountAsync(registered.Id, EntitlementAuditActions.Registered, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(registered.Id, EntitlementAuditActions.OwnershipAccepted, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(registered.Id, EntitlementAuditActions.ChangeProposed, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(registered.Id, EntitlementAuditActions.ChangeApproved, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(registered.Id, EntitlementAuditActions.ChangeRefused, "Denied")).ShouldBe(1);
    }

    [Fact]
    public async Task OnlyTheNamedOwnerOrAMemberOfTheOwningWorkgroupAccepts()
    {
        var app = await ActiveApplicationAsync();
        var personOwned = await RegisterAsync(app.Id, NewKey("p"));
        await using (var scope = Service(Checker))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.AcceptOwnershipAsync(personOwned.Id, personOwned.Draft!.ContentHash, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotOwner);
        }

        var groupOwned = await RegisterAsync(app.Id, NewKey("g"), Settings(ownerKind: "Group", owner: OwnerGroup));
        await using (var scope = Service(Owner))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.AcceptOwnershipAsync(groupOwned.Id, groupOwned.Draft!.ContentHash, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.NotOwner, "not a member of the owning workgroup");
        }

        await using (var scope = Service(Member))
        {
            (await scope.Service.AcceptOwnershipAsync(groupOwned.Id, groupOwned.Draft!.ContentHash, Ct)).Draft.ShouldNotBeNull().OwnerAcceptedBy.ShouldBe(Member);
        }

        // A member of the owning workgroup can't be the second person, nor can the person who accepted.
        var proposed = await ProposeAsync(groupOwned.Id);
        await using (var scope = Service(Member))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.OwnerCannotDecide);
        }

        (await AuditCountAsync(groupOwned.Id, EntitlementAuditActions.ChangeRefused, "Denied")).ShouldBe(2);
    }

    [Fact]
    public async Task TheAuthorTheMakerAndNoLongerAllowedMakersCanNotHaveItApproved()
    {
        var both = new FakePermissions(
            (OtherAuthor, EntitlementAuditActions.ManageAction), (OtherAuthor, EntitlementAuditActions.ApproveAction), (OtherAuthor, EntitlementAuditActions.ReadAction),
            (Author, EntitlementAuditActions.ManageAction), (Author, EntitlementAuditActions.ReadAction), (Owner, EntitlementAuditActions.AcceptAction),
            (Checker, EntitlementAuditActions.ApproveAction));
        var app = await ActiveApplicationAsync();
        var registered = await RegisterAsync(app.Id, NewKey("edited"));

        // Edited by someone who now holds approve too; the owner accepts afterwards, which must not hide who wrote it.
        await using (var scope = Service(OtherAuthor, both))
        {
            await scope.Service.UpdateDraftAsync(registered.Id, registered.Draft!.ConcurrencyVersion, Settings(description: "Edited"), Ct);
        }

        var accepted = await AcceptAsync(registered.Id);
        var proposed = await ProposeAsync(registered.Id);
        await using (var scope = Service(OtherAuthor, both))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.MakerCannotDecide, "the content's last editor, although the owner touched the row since");
        }

        await using (var scope = Service(Checker, new FakePermissions((Checker, EntitlementAuditActions.ApproveAction))))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.MakerNoLongerAllowed);
        }

        await using (var scope = Service(Checker))
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, new string('0', 64), null, Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.DraftChanged);
            (await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct)).State.ShouldBe("Approved");
        }

        accepted.Draft.ShouldNotBeNull().OwnerAcceptedBy.ShouldBe(Owner);
    }

    [Fact]
    public async Task AnInactiveApplicationOrATakenTechnicalNameBlocksActivation()
    {
        var inactive = await RegisterApplicationAsync();
        var waiting = await RegisterAsync(inactive.Id, NewKey("early"));
        await AcceptAsync(waiting.Id);
        await using (var scope = Service())
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeAsync(waiting.Id, "Ready", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.ApplicationNotActive);
        }

        var app = await ActiveApplicationAsync();
        var first = await RegisterAsync(app.Id, NewKey("first"), Settings(technicalName: "CN=Shared Name,OU=G,DC=corp,DC=test"));
        await ActivateAsync(first.Id);
        var second = await RegisterAsync(app.Id, NewKey("second"), Settings(technicalName: "cn=shared name,ou=g,dc=corp,dc=test"));
        await AcceptAsync(second.Id);
        await using (var scope = Service())
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeAsync(second.Id, "Ready", Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.TechnicalNameInUse, "another spelling of the same directory name");
        }
    }

    [Fact]
    public async Task PrivilegedRulesAreRefusedByTheServiceAndTheDatabase()
    {
        var app = await ActiveApplicationAsync();
        await using (var scope = Service())
        {
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RegisterAsync(app.Id, NewKey("priv"), "Admins", Settings(privileged: true, classes: ["Primary", "Secondary"]), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.PrivilegedOnPrimary);
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RegisterAsync(app.Id, NewKey("priv"), "Admins", Settings(privileged: true, classes: ["Secondary"], mode: "NoExpiry"), Ct)))
                .Refusal.ShouldBe(ApplicationRefusal.PrivilegedNeedsEndDate);
            (await Should.ThrowAsync<ApplicationRejectedException>(() => scope.Service.RegisterAsync(app.Id, NewKey("esc"), "X", Settings(escalation: "no_such_group"), Ct)))
                .Field.ShouldBe("EscalationWorkgroup");
        }

        var registered = await RegisterAsync(app.Id, NewKey("dbcheck"));
        var version = registered.Draft!.VersionId;
        foreach (var set in new[]
        {
            $"{Col("Privileged")} = {True}",
            $"{Col("AllowedAccountClasses")} = 'Service,Primary'",
            $"{Col("AllowedAccountClasses")} = 'Secondary'",
            $"{Col("AccessForMode")} = 'FixedDays'",
            $"{Col("OwnerAcceptedBy")} = 'someone'",
            $"{Col("Risk")} = 'Extreme'",
        })
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.CatalogAppConnectionString, $"UPDATE {Table("EntitlementVersion")} SET {set} WHERE {Col("Id")} = '{version}'", Ct), set))
                .Message.ShouldMatch("(?i)(check|constraint)");
        }
    }

    [Fact]
    public async Task TheDatabaseRefusesActivationWithoutAProperApprovalAndAnyChangeAfterIt()
    {
        var app = await ActiveApplicationAsync();
        var registered = await RegisterAsync(app.Id, NewKey("forged"));
        var accepted = await AcceptAsync(registered.Id);
        var draft = accepted.Draft!;

        foreach (var login in Logins)
        {
            // Straight to Active, with no request.
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, $"UPDATE {Table("EntitlementVersion")} SET {Col("State")} = 'Active', {Col("ActivatedByRequestId")} = '{Guid.CreateVersion7()}' WHERE {Col("Id")} = '{draft.VersionId}'", Ct)))
                .Message.ShouldNotBeNullOrEmpty();
        }

        // A forged approved request decided by the content's author, then by the person who accepted: both refused.
        foreach (var checker in new[] { Author, Owner })
        {
            var forged = await ForgeRequestAsync(registered.Id, draft, checker);
            if (forged is { } requestId)
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
                    db.CatalogAppConnectionString,
                    $"UPDATE {Table("EntitlementVersion")} SET {Col("State")} = 'Active', {Col("ActivatedByRequestId")} = '{requestId}' WHERE {Col("Id")} = '{draft.VersionId}'",
                    Ct), checker)).Message.ShouldMatch("(?i)(DL-087|51124)");
            }
        }

        // Activated properly, the version and its request no longer change, and nothing is deleted.
        var requestId2 = (await ProposeAsync(registered.Id)).Id;
        await using (var scope = Service(Checker))
        {
            await scope.Service.ApproveAsync(requestId2, draft.ContentHash, null, Ct);
        }

        foreach (var login in Logins)
        {
            foreach (var sql in new[]
            {
                $"UPDATE {Table("EntitlementVersion")} SET {Col("TechnicalName")} = 'CN=Other' WHERE {Col("Id")} = '{draft.VersionId}'",
                $"UPDATE {Table("EntitlementVersion")} SET {Col("OwnerId")} = 'someone-else' WHERE {Col("Id")} = '{draft.VersionId}'",
                $"DELETE FROM {Table("EntitlementVersion")} WHERE {Col("Id")} = '{draft.VersionId}'",
                $"UPDATE {Table("EntitlementChangeRequest")} SET {Col("Checker")} = 'someone' WHERE {Col("Id")} = '{requestId2}'",
                $"DELETE FROM {Table("EntitlementChangeRequest")} WHERE {Col("Id")} = '{requestId2}'",
                $"UPDATE {Table("Entitlement")} SET {Col("Key")} = 'renamed_key' WHERE {Col("Id")} = '{registered.Id}'",
                $"DELETE FROM {Table("Entitlement")} WHERE {Col("Id")} = '{registered.Id}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldNotBeNullOrEmpty();
            }
        }

        if (!IsSqlServer)
        {
            foreach (var table in new[] { "entitlement", "entitlement_version", "entitlement_change_request" })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE catalog.{table} CASCADE", Ct), table)).Message.ShouldMatch("(?i)DL-087");
            }
        }
    }

    [Fact]
    public async Task KeepingTheOwnerNeedsNoAcceptanceAndANewOwnerDoes()
    {
        var app = await ActiveApplicationAsync();
        var registered = await RegisterAsync(app.Id, NewKey("v2"));
        await ActivateAsync(registered.Id);

        await using (var scope = Service())
        {
            var started = await scope.Service.StartNewDraftAsync(registered.Id, Settings(mode: "FixedDays", days: 60), Ct);
            started.DraftNeedsAcceptance.ShouldBeFalse();
            var request = await scope.Service.ProposeAsync(registered.Id, "Fixed period", Ct);
            request.AcceptedBy.ShouldBeNull();
            await using var checker = Service(Checker);
            await checker.Service.ApproveAsync(request.Id, request.ContentHash, null, Ct);
        }

        await using (var scope = Service())
        {
            var found = (await scope.Service.FindAsync(app.Key, registered.Key, Ct)).ShouldNotBeNull();
            found.Active.ShouldNotBeNull().VersionNumber.ShouldBe(2);
            found.Active.AccessFor.ShouldBe(new AccessForView("FixedDays", 60, null, null));
            (await scope.Service.StartNewDraftAsync(registered.Id, Settings(owner: "next-owner"), Ct)).DraftNeedsAcceptance.ShouldBeTrue();
            (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.ProposeAsync(registered.Id, "New owner", Ct))).Refusal.ShouldBe(ApplicationRefusal.OwnerNotAccepted);
        }
    }

    [Fact]
    public async Task ADraftIsFrozenWhileItsRequestIsPending()
    {
        var app = await ActiveApplicationAsync();
        var registered = await RegisterAsync(app.Id, NewKey("frozen"));
        var accepted = await AcceptAsync(registered.Id);
        await ProposeAsync(registered.Id);
        await using var scope = Service();
        (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.UpdateDraftAsync(registered.Id, accepted.Draft!.ConcurrencyVersion, Settings(description: "Late"), Ct)))
            .Refusal.ShouldBe(ApplicationRefusal.AlreadyPending);
        (await Should.ThrowAsync<ApplicationRefusedException>(() => scope.Service.RenameAsync(registered.Id, accepted.ConcurrencyVersion, "Late name", Ct)))
            .Refusal.ShouldBe(ApplicationRefusal.AlreadyPending);
    }

    /// <summary>
    /// DL-095: superseding is the only change to an active version, and it cannot carry a change to a nullable column (NULL to
    /// a value or back) or a technical name that differs only by case or a trailing space. SQL Server compared these in a way
    /// that let them through; PostgreSQL compares the row with IS NOT DISTINCT FROM. Both refuse, as the app login and as the
    /// schema owner.
    /// </summary>
    [Fact]
    public async Task SupersedingCannotCarryANullOrCaseChange()
    {
        var app = await ActiveApplicationAsync();
        var accepted = await RegisterAsync(app.Id, NewKey("nullsafe_a"), Settings(technicalName: "CN=Null Safe A,OU=Groups,DC=corp,DC=test"));
        await ActivateAsync(accepted.Id);

        // Keeping the owner needs no acceptance, so version 2 is active with no OwnerAcceptedBy.
        var unaccepted = await RegisterAsync(app.Id, NewKey("nullsafe_n"), Settings(technicalName: "CN=Null Safe N,OU=Groups,DC=corp,DC=test"));
        await ActivateAsync(unaccepted.Id);
        await using (var scope = Service())
        {
            await scope.Service.StartNewDraftAsync(unaccepted.Id, Settings(technicalName: "CN=Null Safe N,OU=Groups,DC=corp,DC=test", mode: "FixedDays", days: 60), Ct);
            var request = await scope.Service.ProposeAsync(unaccepted.Id, "Fixed period", Ct);
            request.AcceptedBy.ShouldBeNull();
            await using var checker = Service(Checker);
            await checker.Service.ApproveAsync(request.Id, request.ContentHash, null, Ct);
        }

        var acceptedVersion = await ActiveVersionIdAsync(accepted.Id);
        var unacceptedVersion = await ActiveVersionIdAsync(unaccepted.Id);
        (await OwnerAcceptedByAsync(acceptedVersion)).ShouldBe(Owner);
        (await OwnerAcceptedByAsync(unacceptedVersion)).ShouldBeNull();

        var supersede = $"{Col("State")} = 'Superseded'";
        var at = "'2026-11-21T00:00:00+00:00'";
        foreach (var login in Logins)
        {
            foreach (var (version, set) in new[]
            {
                (unacceptedVersion, $"{supersede}, {Col("OwnerAcceptedBy")} = 'mallory', {Col("OwnerAcceptedAtUtc")} = {at}"),
                (acceptedVersion, $"{supersede}, {Col("OwnerAcceptedBy")} = NULL, {Col("OwnerAcceptedAtUtc")} = NULL"),
                (acceptedVersion, $"{supersede}, {Col("TechnicalName")} = UPPER({Col("TechnicalName")})"),
                (acceptedVersion, $"{supersede}, {Col("TechnicalName")} = {Concat(Col("TechnicalName"), "' '")}"),
            })
            {
                var sql = $"UPDATE {Table("EntitlementVersion")} SET {set} WHERE {Col("Id")} = '{version}'";
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldMatch("(?i)DL-087", sql);
            }
        }

        (await OwnerAcceptedByAsync(acceptedVersion)).ShouldBe(Owner);
        (await OwnerAcceptedByAsync(unacceptedVersion)).ShouldBeNull();

        // Control: superseding with nothing else changed is still allowed, both with and without an acceptance.
        foreach (var version in new[] { acceptedVersion, unacceptedVersion })
        {
            await db.ExecuteAsync(db.CatalogAppConnectionString, $"UPDATE {Table("EntitlementVersion")} SET {supersede} WHERE {Col("Id")} = '{version}'", Ct);
        }
    }

    private string[] Logins => [db.CatalogAppConnectionString, db.MigratorConnectionString];

    private string True => IsSqlServer ? "1" : "TRUE";

    private static EntitlementSettingsText Settings(
        string owner = Owner,
        string ownerKind = "Person",
        bool privileged = false,
        string[]? classes = null,
        string mode = "RequesterChooses",
        int? days = null,
        string escalation = "grp_ent_escalation",
        string description = "Read access to the finance share",
        string technicalName = "CN=Finance Readers,OU=Groups,DC=corp,DC=test") =>
        new(
            "SecurityGroup",
            technicalName,
            description,
            ownerKind,
            owner,
            escalation,
            privileged,
            "Medium",
            classes ?? ["Service", "Primary"],
            mode,
            days,
            mode == "RequesterChooses" ? 30 : null,
            mode == "RequesterChooses" ? 90 : null);

    private static string NewKey(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private string Table(string name) => IsSqlServer ? $"[catalog].[{name}]" : $"catalog.{IamModelConventions.ToSnakeCase(name)}";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

    private string Concat(string left, string right) => IsSqlServer ? $"{left} + {right}" : $"{left} || {right}";

    private async Task<Guid> ActiveVersionIdAsync(Guid entitlementId)
    {
        var value = await db.ScalarAsync(
            db.CatalogAppConnectionString,
            $"SELECT {Col("Id")} FROM {Table("EntitlementVersion")} WHERE {Col("EntitlementId")} = '{entitlementId}' AND {Col("State")} = 'Active'",
            Ct);
        return value is Guid id ? id : Guid.Parse((string)value!, CultureInfo.InvariantCulture);
    }

    private async Task<string?> OwnerAcceptedByAsync(Guid versionId) =>
        await db.ScalarAsync(
            db.CatalogAppConnectionString,
            $"SELECT {Col("OwnerAcceptedBy")} FROM {Table("EntitlementVersion")} WHERE {Col("Id")} = '{versionId}'",
            Ct) as string;

    /// <summary>
    /// Inserts an approved request by SQL. Its own CHECK already refuses a checker who is the owner person or the acceptor, so
    /// for those the insert itself fails and there is nothing to activate with (returns null).
    /// </summary>
    private async Task<Guid?> ForgeRequestAsync(Guid entitlementId, EntitlementVersionView draft, string checker)
    {
        var id = Guid.CreateVersion7();
        string[] columns = ["Id", "EntitlementId", "VersionId", "VersionNumber", "ContentHash", "OwnerKind", "OwnerId", "AcceptedBy", "Maker", "Reason", "ProposedAtUtc", "State", "Checker", "DecidedAtUtc", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version"];
        var sql = $"INSERT INTO {Table("EntitlementChangeRequest")} ({string.Join(", ", columns.Select(Col))}) VALUES ('{id}', '{entitlementId}', '{draft.VersionId}', {draft.VersionNumber}, '{draft.ContentHash}', 'Person', '{Owner}', '{Owner}', 'maker', 'r', '2026-11-16T00:00:00+00:00', 'Approved', '{checker}', '2026-11-16T00:00:00+00:00', '2026-11-16T00:00:00+00:00', 't', '2026-11-16T00:00:00+00:00', 't', 1)";
        try
        {
            await db.ExecuteAsync(db.CatalogAppConnectionString, sql, Ct);
            return id;
        }
        catch (Exception ex) when (ex.Message.Contains("CK_EntitlementChangeRequest_State", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("ck_entitlement_change_request_state", StringComparison.OrdinalIgnoreCase))
        {
            checker.ShouldBe(Owner, "only the owner-and-acceptor case is refused by the request's own check");
            return null;
        }
    }

    private async Task<ApplicationView> RegisterApplicationAsync()
    {
        await using var scope = ApplicationService(Author);
        return await scope.Service.RegisterAsync(
            new RegisterApplicationRequest(NewKey("app"), "Application", new ApplicationSettingsInput("app-owner", [new ApplicationRoleInput("it_admin", RoleRequirement.Required, RoleHolderKind.Group, "grp_it")])), Ct);
    }

    private async Task<ApplicationView> ActiveApplicationAsync()
    {
        var app = await RegisterApplicationAsync();
        ApplicationSettingsRequestView proposed;
        await using (var scope = ApplicationService(Author))
        {
            proposed = await scope.Service.ProposeAsync(app.Id, "Ready", Ct);
        }

        await using (var scope = ApplicationService(Checker))
        {
            await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct);
        }

        return app;
    }

    private async Task<EntitlementView> RegisterAsync(Guid applicationId, string key, EntitlementSettingsText? settings = null)
    {
        await using var scope = Service();
        return await scope.Service.RegisterAsync(applicationId, key, "Entitlement " + key, settings ?? Settings(), Ct);
    }

    private async Task<EntitlementView> AcceptAsync(Guid entitlementId)
    {
        await using var scope = Service(Owner);
        var hash = await DraftHashAsync(entitlementId);
        return await scope.Service.AcceptOwnershipAsync(entitlementId, hash, Ct);
    }

    private async Task<string> DraftHashAsync(Guid entitlementId) =>
        (string)(await db.ScalarAsync(
            db.CatalogAppConnectionString,
            $"SELECT {Col("ContentHash")} FROM {Table("EntitlementVersion")} WHERE {Col("EntitlementId")} = '{entitlementId}' AND {Col("State")} = 'Draft'",
            Ct))!;

    private async Task<EntitlementChangeRequestView> ProposeAsync(Guid entitlementId)
    {
        await using var scope = Service();
        return await scope.Service.ProposeAsync(entitlementId, "Ready for use", Ct);
    }

    private async Task ActivateAsync(Guid entitlementId)
    {
        await AcceptAsync(entitlementId);
        var proposed = await ProposeAsync(entitlementId);
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

    private (CatalogDbContext Context, AuditWriter Writer) Parts(string? actor)
    {
        var context = TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider),
            TimeProvider.System,
            new FixedActor(actor),
            new AuditActionCatalog(CatalogAuditActions.All.Concat(EntitlementAuditActions.All).Select(a => new AuditActionRegistration(a))));
        return (context, writer);
    }

    private Scope<EntitlementCatalogAdministrationService> Service(string? actor = Author, FakePermissions? permissions = null)
    {
        var (context, writer) = Parts(actor);
        return new(context, new EntitlementCatalogAdministrationService(
            context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions ?? DefaultPermissions, new FakeWorkgroups()));
    }

    private Scope<ApplicationCatalogAdministrationService> ApplicationService(string actor)
    {
        var (context, writer) = Parts(actor);
        return new(context, new ApplicationCatalogAdministrationService(
            context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), DefaultPermissions, new FakeWorkgroups()));
    }

    /// <summary>Workgroups as Organization would report them: every key exists except <c>no_such_group</c>; the owning group has one active member.</summary>
    private sealed class FakeWorkgroups : IWorkgroupDirectory
    {
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(key != "no_such_group");

        public Task<WorkgroupMembership?> FindActiveAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(key == OwnerGroup ? new WorkgroupMembership(key, 1, new string('a', 64), [Member]) : null);
    }

    private sealed class FakePermissions(params (string Person, string Action)[] grants) : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) =>
            Task.FromResult(grants.Contains((subject, action)));
    }

    private sealed class Scope<TService>(CatalogDbContext context, TService service) : IAsyncDisposable
    {
        public TService Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
