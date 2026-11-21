using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Account.Application.Accounts;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Account.Infrastructure.Accounts;
using IAM.Modules.Account.Infrastructure.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Identity.Contracts;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 8 slice 2 account behaviour that must be identical on SQL Server and PostgreSQL (DL-089, spec v3.2 §3.1 to §3.3):
/// existing accounts are linked by two people, one live Primary per person, unique live names and target ids, naming rules
/// activated by a second person and evaluated fail-closed, and the database refuses the same things by SQL.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class AccountParityTests(ParityDatabase db)
{
    private const string Admin = "acct-admin";
    private const string Checker = "acct-checker";
    private static readonly Guid InactiveApp = Guid.Parse("0193b000-0000-7000-8000-000000000001");

    private static readonly FakePermissions DefaultPermissions = new(
        (Admin, AccountAuditActions.ManageAction),
        (Admin, AccountAuditActions.ReadAction),
        (Checker, AccountAuditActions.ApproveAction),
        (Checker, AccountAuditActions.ReadAction),
        ("ana", AccountAuditActions.ApproveAction));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task TheMigrationAppliesOnce()
    {
        db.AccountFirstRunApplied[0].ShouldEndWith("_Accounts");
        db.AccountFirstRunApplied.Count.ShouldBe(IsSqlServer ? 3 : 2, "SQL Server has the hyphen fix (DL-091)");
        db.AccountFirstRunApplied[^1].ShouldEndWith("_AccountsCreated");
        db.AccountSecondRunPending.ShouldBeEmpty();
        var tables = IsSqlServer
            ? "SELECT STRING_AGG(TABLE_NAME, ',') FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'account'"
            : "SELECT string_agg(table_name, ',') FROM information_schema.tables WHERE table_schema = 'account'";
        string[] expected = IsSqlServer
            ? ["Account", "AccountLinkRequest", "NamingRule", "NamingRuleChangeRequest", "NamingRuleVersion", "__EFMigrationsHistory"]
            : ["__ef_migrations_history", "account", "account_link_request", "naming_rule", "naming_rule_change_request", "naming_rule_version"];
        ((string)(await db.ScalarAsync(db.MigratorConnectionString, tables, Ct))!).Split(',').Order(StringComparer.Ordinal).ShouldBe(expected.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task AnExistingAccountIsLinkedByASecondPersonAndStaysUnverified()
    {
        var app = Guid.CreateVersion7();
        var name = NewName("ana");
        var proposed = await ProposeAsync(app, "Primary", name, "ana");
        proposed.State.ShouldBe("Proposed");

        await using (var scope = Service(Admin))
        {
            (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ApproveLinkAsync(proposed.Id, null, Ct))).Refusal.ShouldBe(AccountRefusal.MakerCannotDecide);
        }

        await using (var scope = Service("ana"))
        {
            (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ApproveLinkAsync(proposed.Id, null, Ct))).Refusal.ShouldBe(AccountRefusal.OwnerCannotDecide);
        }

        var approved = await ApproveAsync(proposed.Id);
        approved.State.ShouldBe("Approved");
        var accountId = approved.AccountId.ShouldNotBeNull();

        await using (var scope = Service(Admin))
        {
            var account = (await scope.Service.FindAccountAsync(accountId, Ct)).ShouldNotBeNull();
            account.ShouldBe(new AccountView(accountId, app, "Primary", name, "CN=" + name, "ana", "Active", "Linked", proposed.Id, false));
            (await scope.Service.ListAccountsAsync(app, "ana", Ct)).ShouldHaveSingleItem().Id.ShouldBe(accountId);
        }

        await using (var context = Context(Admin))
        {
            var record = (await new AccountDirectory(context).FindAsync(accountId, Ct)).ShouldNotBeNull();
            record.Verified.ShouldBeFalse();
            (await new AccountDirectory(context).ForOwnerAsync(app, "ana", Ct)).ShouldHaveSingleItem();
        }

        (await AuditCountAsync(proposed.Id, AccountAuditActions.LinkProposed, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(proposed.Id, AccountAuditActions.LinkApproved, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(proposed.Id, AccountAuditActions.LinkRefused, "Denied")).ShouldBe(2);
    }

    [Fact]
    public async Task NamesWithHyphensDotsAndUnderscoresAreAccepted()
    {
        var app = Guid.CreateVersion7();
        var name = $"ana-lim_x.{Guid.NewGuid().ToString("N")[..6]}";
        var linked = await ApproveAsync((await ProposeAsync(app, "Primary", name, "hy-" + name)).Id);
        await using var scope = Service(Admin);
        (await scope.Service.FindAccountAsync(linked.AccountId!.Value, Ct)).ShouldNotBeNull().Name.ShouldBe(name);
    }

    [Fact]
    public async Task OnePrimaryPerPersonAndUniqueNamesAndTargets()
    {
        var app = Guid.CreateVersion7();
        var name = NewName("bo");
        await ApproveAsync((await ProposeAsync(app, "Primary", name, "bo")).Id);

        await using var scope = Service(Admin);
        (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ProposeLinkAsync(app, "Primary", NewName("bo2"), "CN=other", "bo", "Second", Ct)))
            .Refusal.ShouldBe(AccountRefusal.PrimaryExists);
        (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ProposeLinkAsync(app, "Secondary", name.ToUpperInvariant(), "CN=other2", "bo", "Admin", Ct)))
            .Refusal.ShouldBe(AccountRefusal.NameInUse, "another spelling of the same name");
        (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ProposeLinkAsync(app, "Secondary", NewName("bo-adm"), "CN=" + name, "bo", "Admin", Ct)))
            .Refusal.ShouldBe(AccountRefusal.TargetInUse);

        // Several other accounts are fine; the same name pending twice is not.
        var secondary = await scope.Service.ProposeLinkAsync(app, "Secondary", NewName("bo-adm"), "CN=bo-adm", "bo", "Admin account", Ct);
        (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ProposeLinkAsync(app, "Service", secondary.Name, "CN=svc", "bo", "Same name", Ct)))
            .Refusal.ShouldBe(AccountRefusal.AlreadyPending);
        (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ProposeLinkAsync(InactiveApp, "Primary", NewName("x"), "CN=x", "x", "Inactive", Ct)))
            .Refusal.ShouldBe(AccountRefusal.ApplicationNotActive);
    }

    [Fact]
    public async Task TwoPendingLinksForOnePrimaryCanNotBothBeApproved()
    {
        // Two link requests for the same person proposed before either is approved: the second approval finds the first account.
        var app = Guid.CreateVersion7();
        var first = await ProposeAsync(app, "Primary", NewName("cy"), "cy");
        var second = await ProposeAsync(app, "Primary", NewName("cy-b"), "cy");
        await ApproveAsync(first.Id);
        await using var scope = Service(Checker);
        (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ApproveLinkAsync(second.Id, null, Ct))).Refusal.ShouldBe(AccountRefusal.PrimaryExists);
    }

    [Fact]
    public async Task TheDatabaseRefusesAccountsNoApprovedLinkNamesAndAnyChangeToWhoOwnsThem()
    {
        var app = Guid.CreateVersion7();
        var linked = await ApproveAsync((await ProposeAsync(app, "Secondary", NewName("dee"), "dee")).Id);
        var accountId = linked.AccountId!.Value;
        var pending = await ProposeAsync(app, "Primary", NewName("eve"), "eve");

        foreach (var login in Logins)
        {
            foreach (var sql in new[]
            {
                AccountInsert(app, pending.Id, pending.Name, "eve", "Primary"),
                AccountInsert(app, linked.Id, "other.name", "dee", "Secondary"),
                $"UPDATE {Table("Account")} SET {Col("Owner")} = 'mallory' WHERE {Col("Id")} = '{accountId}'",
                $"UPDATE {Table("Account")} SET {Col("Type")} = 'Primary' WHERE {Col("Id")} = '{accountId}'",
                $"UPDATE {Table("Account")} SET {Col("NameKey")} = 'x' WHERE {Col("Id")} = '{accountId}'",
                $"DELETE FROM {Table("Account")} WHERE {Col("Id")} = '{accountId}'",
                $"UPDATE {Table("AccountLinkRequest")} SET {Col("Checker")} = 'someone' WHERE {Col("Id")} = '{linked.Id}'",
                $"UPDATE {Table("AccountLinkRequest")} SET {Col("Owner")} = 'mallory' WHERE {Col("Id")} = '{pending.Id}'",
                $"DELETE FROM {Table("AccountLinkRequest")} WHERE {Col("Id")} = '{pending.Id}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldNotBeNullOrEmpty();
            }
        }

        // The owner approving their own link is refused by the request's own CHECK.
        (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
            db.AccountAppConnectionString,
            $"UPDATE {Table("AccountLinkRequest")} SET {Col("State")} = 'Approved', {Col("Checker")} = 'eve', {Col("DecidedAtUtc")} = '2026-11-16T00:00:00+00:00' WHERE {Col("Id")} = '{pending.Id}'",
            Ct))).Message.ShouldMatch("(?i)(check|constraint)");

        if (!IsSqlServer)
        {
            foreach (var table in new[] { "account", "account_link_request", "naming_rule", "naming_rule_version", "naming_rule_change_request" })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE account.{table} CASCADE", Ct), table)).Message.ShouldMatch("(?i)DL-089");
            }
        }
    }

    [Fact]
    public async Task ANamingRuleIsActivatedByASecondPersonAndNamesAccountsFailClosed()
    {
        var app = Guid.CreateVersion7();
        await using (var scope = Service(Admin))
        {
            (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.SetNamingDraftAsync(app, "Primary", null, [Part("unpublished_attr")], 30, Ct)))
                .Refusal.ShouldBe(AccountRefusal.UnknownAttribute);
            (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.SetNamingDraftAsync(app, "Service", null, FirstDotLast, 30, Ct)))
                .Refusal.ShouldBe(AccountRefusal.NoNamingRuleForType);
            var rule = await scope.Service.SetNamingDraftAsync(app, "Primary", null, FirstDotLast, 30, Ct);
            rule.Draft.ShouldNotBeNull().EditedBy.ShouldBe(Admin);
        }

        NamingRuleChangeRequestView proposed;
        await using (var scope = Service(Admin))
        {
            proposed = await scope.Service.ProposeNamingRuleAsync(app, "Primary", "Standard names", Ct);
        }

        // A checker who wrote the draft can't approve it.
        var both = new FakePermissions((Admin, AccountAuditActions.ApproveAction), (Admin, AccountAuditActions.ManageAction), (Checker, AccountAuditActions.ApproveAction), (Checker, AccountAuditActions.ManageAction));
        await using (var scope = Service(Checker, both))
        {
            (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ApproveNamingRuleAsync(proposed.Id, new string('0', 64), null, Ct)))
                .Refusal.ShouldBe(AccountRefusal.DraftChanged);
            (await scope.Service.ApproveNamingRuleAsync(proposed.Id, proposed.ContentHash, null, Ct)).State.ShouldBe("Approved");
        }

        await using (var context = Context(Admin))
        {
            var naming = new AccountNaming(context);
            (await naming.ProposeAsync(app, "Primary", Person("Ána", "Lim"), Ct)).ShouldBe(new IAM.Modules.Account.Contracts.ProposedAccountName("ana.lim", null, 1, proposed.ContentHash));
            (await naming.ProposeAsync(app, "Primary", new Dictionary<string, string?> { ["given_name"] = "Ana" }, Ct)).Failure.ShouldBe("MissingAttribute");
            (await naming.ProposeAsync(app, "Secondary", Person("Ana", "Lim"), Ct)).Failure.ShouldBe(AccountNaming.NoRule);
            (await naming.ProposeAsync(app, "Service", Person("Ana", "Lim"), Ct)).Failure.ShouldBe(AccountNaming.NoRule);
        }

        // Once a live account holds the name, the rule's name is refused (DL-086: fail closed, a person gives another name).
        await ApproveAsync((await ProposeAsync(app, "Primary", "Ana.Lim", "ana-lim-1")).Id);
        await using (var context = Context(Admin))
        {
            (await new AccountNaming(context).ProposeAsync(app, "Primary", Person("Ana", "Lim"), Ct)).Failure.ShouldBe(AccountNaming.NameInUse);
        }

        (await AuditCountAsync(proposed.NamingRuleId, AccountAuditActions.NamingApproved, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task TheAuthorOfADraftCanNotApproveItAndTheDatabaseAgrees()
    {
        var app = Guid.CreateVersion7();
        var both = new FakePermissions((Admin, AccountAuditActions.ApproveAction), (Admin, AccountAuditActions.ManageAction), (Checker, AccountAuditActions.ManageAction), (Checker, AccountAuditActions.ApproveAction));
        NamingRuleView rule;
        await using (var scope = Service(Admin, both))
        {
            rule = await scope.Service.SetNamingDraftAsync(app, "Secondary", null, FirstDotLast, 30, Ct);
        }

        NamingRuleChangeRequestView proposed;
        await using (var scope = Service(Checker, both))
        {
            proposed = await scope.Service.ProposeNamingRuleAsync(app, "Secondary", "Admin accounts", Ct);
        }

        await using (var scope = Service(Admin, both))
        {
            (await Should.ThrowAsync<AccountRefusedException>(() => scope.Service.ApproveNamingRuleAsync(proposed.Id, proposed.ContentHash, null, Ct)))
                .Refusal.ShouldBe(AccountRefusal.MakerCannotDecide, "the draft's author");
        }

        // Forged by SQL: an approved request decided by the author still can't activate the draft.
        var draft = rule.Draft!;
        var forged = Guid.CreateVersion7();
        string[] columns = ["Id", "NamingRuleId", "VersionId", "VersionNumber", "ContentHash", "Maker", "Reason", "ProposedAtUtc", "State", "Checker", "DecidedAtUtc", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version"];
        await db.ExecuteAsync(
            db.AccountAppConnectionString,
            $"INSERT INTO {Table("NamingRuleChangeRequest")} ({string.Join(", ", columns.Select(Col))}) VALUES ('{forged}', '{rule.Id}', '{draft.VersionId}', {draft.VersionNumber}, '{draft.ContentHash}', 'maker', 'r', '2026-11-16T00:00:00+00:00', 'Approved', '{Admin}', '2026-11-16T00:00:00+00:00', '2026-11-16T00:00:00+00:00', 't', '2026-11-16T00:00:00+00:00', 't', 1)",
            Ct);
        foreach (var login in Logins)
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
                login,
                $"UPDATE {Table("NamingRuleVersion")} SET {Col("State")} = 'Active', {Col("ActivatedByRequestId")} = '{forged}' WHERE {Col("Id")} = '{draft.VersionId}'",
                Ct))).Message.ShouldMatch("(?i)(DL-089|51211)");
        }

        // Approved properly, the version can no longer change.
        await using (var scope = Service("third", new FakePermissions(("third", AccountAuditActions.ApproveAction), (Checker, AccountAuditActions.ManageAction))))
        {
            await scope.Service.ApproveNamingRuleAsync(proposed.Id, proposed.ContentHash, null, Ct);
        }

        foreach (var login in Logins)
        {
            foreach (var sql in new[]
            {
                $"UPDATE {Table("NamingRuleVersion")} SET {Col("Template")} = '{{}}' WHERE {Col("Id")} = '{draft.VersionId}'",
                $"DELETE FROM {Table("NamingRuleVersion")} WHERE {Col("Id")} = '{draft.VersionId}'",
                $"UPDATE {Table("NamingRule")} SET {Col("AccountType")} = 'Primary' WHERE {Col("Id")} = '{rule.Id}'",
                $"DELETE FROM {Table("NamingRule")} WHERE {Col("Id")} = '{rule.Id}'",
                $"UPDATE {Table("NamingRuleChangeRequest")} SET {Col("Checker")} = 'x' WHERE {Col("Id")} = '{proposed.Id}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldNotBeNullOrEmpty();
            }
        }
    }

    private static readonly IReadOnlyList<NamePartText?> FirstDotLast =
    [
        new NamePartText(null, "given_name", ["RemoveAccents", "LettersDigitsOnly", "Lower"], null),
        new NamePartText(".", null, [], null),
        new NamePartText(null, "family_name", ["RemoveAccents", "LettersDigitsOnly", "Lower"], null),
    ];

    private string[] Logins => [db.AccountAppConnectionString, db.MigratorConnectionString];

    private static NamePartText Part(string attribute) => new(null, attribute, ["Lower"], null);

    private static Dictionary<string, string?> Person(string given, string family) => new() { ["given_name"] = given, ["family_name"] = family };

    private static string NewName(string prefix) => $"{prefix}.{Guid.NewGuid().ToString("N")[..8]}";

    private string Table(string name) => IsSqlServer ? $"[account].[{name}]" : $"account.{IamModelConventions.ToSnakeCase(name)}";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

    private string AccountInsert(Guid app, Guid linkRequestId, string name, string owner, string type)
    {
        string[] columns = ["Id", "ApplicationId", "Type", "Name", "NameKey", "TargetId", "Owner", "Status", "Origin", "LinkRequestId", "Verified", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version"];
        var verified = IsSqlServer ? "0" : "FALSE";
        return $"INSERT INTO {Table("Account")} ({string.Join(", ", columns.Select(Col))}) VALUES ('{Guid.CreateVersion7()}', '{app}', '{type}', '{name}', '{name.ToLowerInvariant()}', 'CN={name}', '{owner}', 'Active', 'Linked', '{linkRequestId}', {verified}, '2026-11-16T00:00:00+00:00', 't', '2026-11-16T00:00:00+00:00', 't', 1)";
    }

    private async Task<AccountLinkRequestView> ProposeAsync(Guid app, string type, string name, string owner)
    {
        await using var scope = Service(Admin);
        return await scope.Service.ProposeLinkAsync(app, type, name, "CN=" + name, owner, "Matches the HR record", Ct);
    }

    private async Task<AccountLinkRequestView> ApproveAsync(Guid requestId)
    {
        await using var scope = Service(Checker);
        return await scope.Service.ApproveLinkAsync(requestId, "Checked", Ct);
    }

    private async Task<long> AuditCountAsync(Guid targetId, string action, string outcome)
    {
        var sql = IsSqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [TargetId] = '{targetId}' AND [Action] = '{action}' AND [Outcome] = '{outcome}'"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE target_id = '{targetId}' AND action = '{action}' AND outcome = '{outcome}'";
        return Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }

    private AccountDbContext Context(string actor) =>
        TestSupport.CreateAccount(db.Provider, db.AccountAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));

    private Scope Service(string actor, FakePermissions? permissions = null)
    {
        var context = Context(actor);
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor), new AuditActionCatalog(AccountAuditActions.All.Select(a => new AuditActionRegistration(a))));
        return new Scope(context, new AccountAdministrationService(
            context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions ?? DefaultPermissions, new FakeApplications(), new FakeAttributes()));
    }

    /// <summary>Every application is active except <see cref="InactiveApp"/>.</summary>
    private sealed class FakeApplications : IApplicationApproverDirectory
    {
        public Task<ApplicationApprovers?> FindActiveAsync(Guid applicationId, CancellationToken cancellationToken) =>
            Task.FromResult(applicationId == InactiveApp ? null : new ApplicationApprovers(applicationId, 1, new string('a', 64), "app-owner", []));
    }

    /// <summary>Only <c>given_name</c> and <c>family_name</c> are published.</summary>
    private sealed class FakeAttributes : IAttributeCatalog
    {
        public Task<PublishedAttributeDefinition?> FindPublishedByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(key is "given_name" or "family_name"
                ? new PublishedAttributeDefinition(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, key, key, "String", "Person", "hr", "Internal", "None", 1440, 100, ["Eq"], new string('b', 64))
                : null);

        public Task<PublishedAttributeDefinition?> FindPublishedByIdAsync(Guid attributeDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult<PublishedAttributeDefinition?>(null);
    }

    private sealed class FakePermissions(params (string Person, string Action)[] grants) : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) =>
            Task.FromResult(grants.Contains((subject, action)));
    }

    private sealed class Scope(AccountDbContext context, AccountAdministrationService service) : IAsyncDisposable
    {
        public AccountAdministrationService Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
