using System.Globalization;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Account.Application.Accounts;
using IAM.Modules.Account.Infrastructure.Accounts;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Application.Applications;
using IAM.Modules.Catalog.Application.Entitlements;
using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Catalog.Infrastructure.Entitlements;
using IAM.Modules.Identity.Contracts;
using IAM.Modules.Identity.Infrastructure.Attributes;
using IAM.Modules.Organization.Contracts;
using IAM.Modules.Organization.Infrastructure.Workgroups;
using IAM.Modules.Policy.Contracts;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Domain.Items;
using IAM.Modules.Request.Infrastructure.Items;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 8 slice 4a request items, end to end against SQL Server and PostgreSQL (DL-091): the real catalog, accounts and
/// workflow; a fixed matrix selection (the matrix has its own tests, and one database holds one matrix). An item is checked,
/// frozen with its outbox message, and the workflow consumer starts its approval once, with the entitlement owner resolved.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed partial class RequestItemParityTests(ParityDatabase db, RabbitMqBroker broker) : IClassFixture<RabbitMqBroker>, IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private const string Admin = "req-admin";
    private const string Checker = "req-checker";
    private const string Helper = "req-helper";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task AnItemIsFrozenWithItsOutboxMessageAndTheWorkflowStartsItsApprovalOnce()
    {
        var world = await WorldAsync();
        var matrix = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("ApplicationOwner", sequence: 2)));

        RequestItemView item;
        await using (var scope = Service(world.Beneficiary, matrix))
        {
            item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, 45), Ct);
            item.DurationDays.ShouldBe(45);
            item.State.ShouldBe("Submitted");
            (await scope.Service.FindAsync(item.Id, Ct)).ShouldNotBeNull();
            (await scope.Service.MineAsync(Ct)).ShouldContain(i => i.Id == item.Id);
        }

        // Nobody else sees it without the read permission.
        await using (var scope = Service("someone-else", matrix))
        {
            (await scope.Service.FindAsync(item.Id, Ct)).ShouldBeNull();
        }

        var envelope = await OutboxEnvelopeAsync(item.Id);
        envelope.Type.ShouldBe(RequestItemSubmitted.MessageType);
        var submitted = MessageEnvelopes.Read<RequestItemSubmitted>(envelope);
        submitted.Stages.Select(s => (s.Source, s.EntitlementId, s.ApplicationId)).ShouldBe([("EntitlementOwner", world.Standard, null), ("ApplicationOwner", null, world.Application)]);
        submitted.PolicyMaker.ShouldBe(FixedMatrix.Maker);

        // The workflow consumer starts the approval with the item's id; the same message again changes nothing.
        var (workflowService, workflowContext) = Workflow();
        await using (workflowContext)
        {
            var consumer = Starter(workflowService);
            (await consumer.HandleAsync(envelope, Ct)).ShouldBe(MessageOutcome.Processed);
            (await consumer.HandleAsync(envelope, Ct)).ShouldBe(MessageOutcome.Duplicate);
            var started = (await workflowService.FindAsync(item.Id, Ct)).ShouldNotBeNull();
            started.RequestItemId.ShouldBe(item.Id);
            started.Stages.First(s => s.Sequence == 1).Candidates.ShouldBe([world.EntitlementOwner], "the entitlement owner is resolved (DL-091)");
        }

        (await AuditCountAsync(item.Id.ToString(), RequestAuditActions.Submitted, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task EveryRuleIsCheckedServerSideAndEveryRefusalIsAudited()
    {
        var world = await WorldAsync();
        var matrix = new FixedMatrix(Route(Stage("EntitlementOwner")));

        async Task Refused(string actor, SubmitItem item, RequestRefusal refusal, IApprovalMatrix? with = null)
        {
            await using var scope = Service(actor, with ?? matrix);
            (await Should.ThrowAsync<RequestRefusedException>(() => scope.Service.SubmitAsync(item, Ct))).Refusal.ShouldBe(refusal, refusal.ToString());
        }

        var b = world.Beneficiary;
        await Refused(Helper, new SubmitItem(b, world.Standard, world.Primary, null, null, null), RequestRefusal.NotForOthers);
        await Refused(b, new SubmitItem(b, Guid.CreateVersion7(), world.Primary, null, null, null), RequestRefusal.EntitlementNotRequestable);
        await Refused(b, new SubmitItem(b, world.Privileged, world.Primary, null, null, null), RequestRefusal.PrivilegedOnPrimary);
        await Refused(b, new SubmitItem(b, world.Standard, world.OthersPrimary, null, null, null), RequestRefusal.AccountNotBeneficiarys);
        await Refused(b, new SubmitItem(b, world.Standard, Guid.CreateVersion7(), null, null, null), RequestRefusal.AccountNotUsable);
        await Refused(b, new SubmitItem(b, world.Standard, null, "Primary", null, null), RequestRefusal.PrimaryExists);
        await Refused(b, new SubmitItem(b, world.Standard, null, "Secondary", null, null), RequestRefusal.AccountTypeNotAllowed);
        await Refused(b, new SubmitItem(b, world.Standard, world.Primary, null, null, 91), RequestRefusal.InvalidDuration);
        await Refused(b, new SubmitItem(b, world.Privileged, null, "Secondary", null, 5), RequestRefusal.InvalidDuration);
        await Refused(b, new SubmitItem(b, world.Standard, world.Primary, null, null, null), RequestRefusal.NoRoute, new FixedMatrix(null));

        // A new Secondary account has no naming rule and no level that may give the name: refused, never guessed (DL-091).
        await Refused(b, new SubmitItem(b, world.Privileged, null, "Secondary", null, null), RequestRefusal.NoAccountName);

        // Someone else's Service account: only its owner may ask.
        await Refused(Helper, new SubmitItem(Helper, world.Standard, null, "Service", "svc-x", null), RequestRefusal.AccountTypeNotAllowed);
        (await AuditCountAsync(null, RequestAuditActions.Refused, "Denied")).ShouldBeGreaterThanOrEqualTo(11);

        // Bad input is a field error, not a refusal.
        await using (var scope = Service(b, matrix))
        {
            (await Should.ThrowAsync<RequestRejectedException>(() => scope.Service.SubmitAsync(new SubmitItem(b, world.Standard, world.Primary, "Primary", null, null), Ct))).Field.ShouldBe("Account");
            (await Should.ThrowAsync<RequestRejectedException>(() => scope.Service.SubmitAsync(new SubmitItem(b, world.Standard, null, "User", null, null), Ct))).Field.ShouldBe("NewAccountType");
            (await Should.ThrowAsync<RequestRejectedException>(() => scope.Service.SubmitAsync(new SubmitItem(b, world.ServiceAllowed, null, "Service", "bad name", null), Ct))).Field.ShouldBe("NewAccountName");
        }
    }

    [Fact]
    public async Task NewAccountsAndTheSameAccessTwice()
    {
        var world = await WorldAsync();

        // A new Secondary account whose name a level will give: accepted, with no name yet.
        var withNameLevel = new FixedMatrix(Route(Stage("Workgroup", workgroup: "iam_account_names", kind: "Action", fields: ["AccountName"]), Stage("EntitlementOwner", sequence: 2)));
        await using (var scope = Service(world.Beneficiary, withNameLevel))
        {
            var item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), Ct);
            item.NewAccountType.ShouldBe("Secondary");
            item.NewAccountName.ShouldBeNull();
            item.DurationDays.ShouldBe(14, "the entitlement fixes the period");
        }

        // A Service account named by its owner, for themselves.
        var matrix = new FixedMatrix(Route(Stage("EntitlementOwner")));
        await using (var scope = Service(Helper, matrix))
        {
            var service = await scope.Service.SubmitAsync(new SubmitItem(Helper, world.ServiceAllowed, null, "Service", "svc-reports", null), Ct);
            service.NewAccountName.ShouldBe("svc-reports");
            service.DurationDays.ShouldBeNull("no expiry");
            (await Should.ThrowAsync<RequestRefusedException>(() => scope.Service.SubmitAsync(new SubmitItem(Helper, world.ServiceAllowed, null, "Service", "svc-other", null), Ct)))
                .Refusal.ShouldBe(RequestRefusal.AlreadyRequested, "the same access for the same person is already waiting");
        }
    }

    [Fact]
    public async Task OnlyAPersonOnTheOpenNameStageGivesTheNameAndOnlyTheName()
    {
        var world = await WorldAsync();

        // The entitlement owner's Action level gives the new Secondary account's name, then the application owner decides.
        var route = new FixedMatrix(Route(Stage("EntitlementOwner", kind: "Action", fields: ["AccountName"]), Stage("ApplicationOwner", sequence: 2)));
        RequestItemView item;
        await using (var scope = Service(world.Beneficiary, route))
        {
            item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), Ct);
            item.AccountName.ShouldBeNull("no naming rule: the level gives it");
        }

        var submitted = MessageEnvelopes.Read<RequestItemSubmitted>(await OutboxEnvelopeAsync(item.Id));
        var nameStage = submitted.Stages.Single(s => s.Kind == "Action").StageId;
        var decisionStage = submitted.Stages.Single(s => s.Kind == "Decision").StageId;

        async Task Refused(string actor, Guid stage, string name, RequestRefusal refusal)
        {
            await using var scope = Service(actor, route);
            (await Should.ThrowAsync<RequestRefusedException>(() => scope.Service.GiveAccountNameAsync(item.Id, stage, name, Ct))).Refusal.ShouldBe(refusal, refusal.ToString());
        }

        // Before the approval starts, nobody is on any stage.
        await Refused(world.EntitlementOwner, nameStage, "adm.ben", RequestRefusal.NotOnStage);
        var (workflow, workflowContext) = Workflow();
        await using (workflowContext)
        {
            (await Starter(workflow).HandleAsync(await OutboxEnvelopeAsync(item.Id), Ct)).ShouldBe(MessageOutcome.Processed);
        }

        await Refused(world.Beneficiary, nameStage, "adm.ben", RequestRefusal.NotOnStage);
        await Refused("someone-else", nameStage, "adm.ben", RequestRefusal.NotOnStage);
        await Refused(world.EntitlementOwner, decisionStage, "adm.ben", RequestRefusal.NotANameStage);
        await Refused(world.EntitlementOwner, Guid.CreateVersion7(), "adm.ben", RequestRefusal.NotANameStage);
        await Refused(world.EntitlementOwner, nameStage, world.PrimaryName.ToUpperInvariant(), RequestRefusal.NameInUse);

        await using (var scope = Service(world.EntitlementOwner, route))
        {
            (await Should.ThrowAsync<RequestRejectedException>(() => scope.Service.GiveAccountNameAsync(item.Id, nameStage, "bad name", Ct))).Field.ShouldBe("Name");
            (await scope.Service.GiveAccountNameAsync(item.Id, nameStage, "adm-ben", Ct)).AccountName.ShouldBe("adm-ben");
            (await scope.Service.GiveAccountNameAsync(item.Id, nameStage, "adm.ben", Ct)).AccountName.ShouldBe("adm.ben", "changed again while the stage is open");
            (await Should.ThrowAsync<RequestRefusedException>(() => scope.Service.GiveAccountNameAsync(item.Id, nameStage, "adm.ben", Ct))).Refusal.ShouldBe(RequestRefusal.NameUnchanged);
        }

        await using (var scope = Service(world.Beneficiary, route))
        {
            var seen = (await scope.Service.FindAsync(item.Id, Ct)).ShouldNotBeNull();
            seen.AccountName.ShouldBe("adm.ben");
            seen.NewAccountName.ShouldBeNull("the frozen item never changes");
            (await scope.Service.MineAsync(Ct)).Single(i => i.Id == item.Id).AccountName.ShouldBe("adm.ben");
        }

        // Once the owner completes the task, the name is settled: nobody can change it.
        var (ownerWorkflow, ownerContext) = Workflow(world.EntitlementOwner);
        await using (ownerContext)
        {
            var instance = (await ownerWorkflow.FindAsync(item.Id, Ct)).ShouldNotBeNull();
            await ownerWorkflow.CompleteAsync(item.Id, instance.Version, Guid.CreateVersion7(), nameStage, "Named", Ct, await SeenNameAsync(item.Id));
        }

        await Refused(world.EntitlementOwner, nameStage, "adm.later", RequestRefusal.NotOnStage);
        (await AuditCountAsync(item.Id.ToString(), RequestAuditActions.AccountNamed, "Succeeded")).ShouldBe(2);

        // The assignments are append-only.
        foreach (var login in new[] { db.RequestAppConnectionString, db.MigratorConnectionString })
        {
            foreach (var sql in new[]
            {
                $"UPDATE {Table("AccountNameAssignment")} SET {Col("Name")} = 'mallory' WHERE {Col("ItemId")} = '{item.Id}'",
                $"DELETE FROM {Table("AccountNameAssignment")} WHERE {Col("ItemId")} = '{item.Id}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldNotBeNullOrEmpty();
            }
        }
    }

    [Fact]
    public async Task NoNameIsGivenWhileTheApprovalIsHeld()
    {
        var world = await WorldAsync();

        // A parallel stage with nobody to approve holds the approval; the name stage stays open, but nothing is done on it.
        var route = new FixedMatrix(Route(Stage("EntitlementOwner", kind: "Action", fields: ["AccountName"]), Stage("Workgroup", workgroup: "no_such_workgroup")));
        RequestItemView item;
        await using (var scope = Service(world.Beneficiary, route))
        {
            item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), Ct);
        }

        var nameStage = MessageEnvelopes.Read<RequestItemSubmitted>(await OutboxEnvelopeAsync(item.Id)).Stages.Single(s => s.Kind == "Action").StageId;
        var (workflow, workflowContext) = Workflow();
        await using (workflowContext)
        {
            (await Starter(workflow).HandleAsync(await OutboxEnvelopeAsync(item.Id), Ct)).ShouldBe(MessageOutcome.Processed);
            var held = (await workflow.FindAsync(item.Id, Ct)).ShouldNotBeNull();
            held.State.ShouldBe("Held");
            held.Stages.Single(s => s.StageId == nameStage).State.ShouldBe("Active");
            held.Stages.Single(s => s.StageId == nameStage).Candidates.ShouldBe([world.EntitlementOwner]);
        }

        await using var named = Service(world.EntitlementOwner, route);
        (await Should.ThrowAsync<RequestRefusedException>(() => named.Service.GiveAccountNameAsync(item.Id, nameStage, "adm.ben", Ct))).Refusal.ShouldBe(RequestRefusal.NotOnStage);
    }

    [Fact]
    public async Task AnItemForAnExistingAccountHasNoNameToGive()
    {
        var world = await WorldAsync();
        var route = new FixedMatrix(Route(Stage("EntitlementOwner", kind: "Action", fields: ["AccountName"]), Stage("ApplicationOwner", sequence: 2)));
        RequestItemView item;
        await using (var scope = Service(world.Beneficiary, route))
        {
            item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null), Ct);
        }

        var stage = MessageEnvelopes.Read<RequestItemSubmitted>(await OutboxEnvelopeAsync(item.Id)).Stages.Single(s => s.Kind == "Action").StageId;
        var (workflow, workflowContext) = Workflow();
        await using (workflowContext)
        {
            await Starter(workflow).HandleAsync(await OutboxEnvelopeAsync(item.Id), Ct);
        }

        await using var named = Service(world.EntitlementOwner, route);
        (await Should.ThrowAsync<RequestRefusedException>(() => named.Service.GiveAccountNameAsync(item.Id, stage, "adm.ben", Ct))).Refusal.ShouldBe(RequestRefusal.NoNewAccount);
    }

    [Fact]
    public async Task TheDatabaseKeepsItemsFrozenAndOutboxMessagesUnchanged()
    {
        var world = await WorldAsync();
        RequestItemView item;
        await using (var scope = Service(world.Beneficiary, new FixedMatrix(Route(Stage("EntitlementOwner")))))
        {
            item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null), Ct);
        }

        foreach (var login in new[] { db.RequestAppConnectionString, db.MigratorConnectionString })
        {
            foreach (var sql in new[]
            {
                $"UPDATE {Table("RequestItem")} SET {Col("Beneficiary")} = 'mallory' WHERE {Col("Id")} = '{item.Id}'",
                $"UPDATE {Table("RequestItem")} SET {Col("EntitlementId")} = '{world.Privileged}' WHERE {Col("Id")} = '{item.Id}'",
                $"DELETE FROM {Table("RequestItem")} WHERE {Col("Id")} = '{item.Id}'",
                $"UPDATE {Table("OutboxMessage")} SET {Col("Payload")} = '{{}}' WHERE {Col("IdempotencyKey")} = 'request.item.submitted:{item.Id:D}'",
                $"DELETE FROM {Table("OutboxMessage")} WHERE {Col("IdempotencyKey")} = 'request.item.submitted:{item.Id:D}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldNotBeNullOrEmpty();
            }
        }

        if (!IsSqlServer)
        {
            // The item table is truncated with the names that reference it, so the guard triggers (not the foreign key) refuse.
            foreach (var tables in new[] { "request.request_item, request.account_name_assignment", "request.account_name_assignment", "request.outbox_message" })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE {tables}", Ct), tables)).Message.ShouldMatch("(?i)DL-0(76|91|92)");
            }
        }
    }

    // ---- The world: an active application, entitlements, and linked accounts --------------------------------------------

    private sealed record World(string Beneficiary, string EntitlementOwner, Guid Application, Guid Standard, Guid Privileged, Guid ServiceAllowed, Guid Primary, Guid OthersPrimary, string PrimaryName);

    private async Task<World> WorldAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var beneficiary = $"ben-{tag}";
        var other = $"other-{tag}";
        var entitlementOwner = $"eowner-{tag}";
        var permissions = new Grants(
            (Admin, AccessActions.ApplicationsManage), (Checker, AccessActions.ApplicationsApprove),
            (Admin, AccessActions.EntitlementsManage), (Admin, AccessActions.EntitlementsRead), (Checker, AccessActions.EntitlementsApprove),
            (entitlementOwner, AccessActions.EntitlementOwnershipAccept),
            (Admin, AccessActions.AccountsManage), (Checker, AccessActions.AccountsApprove));

        // Application, active.
        ApplicationView app;
        await using (var c = Catalog(Admin, permissions))
        {
            app = await c.Applications.RegisterAsync(new RegisterApplicationRequest($"app_{tag}", "App", new ApplicationSettingsInput($"aowner-{tag}", [new ApplicationRoleInput("it_admin", RoleRequirement.Required, RoleHolderKind.Group, "grp_it")])), Ct);
            var proposed = await c.Applications.ProposeAsync(app.Id, "Ready", Ct);
            await using var k = Catalog(Checker, permissions);
            await k.Applications.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct);
        }

        async Task<Guid> EntitlementAsync(string key, bool privileged, string[] types, string mode, int? days, int? dflt, int? max)
        {
            EntitlementView e;
            await using (var r = Catalog(Admin, permissions))
            {
                e = await r.Entitlements.RegisterAsync(app.Id, $"{key}_{tag}", key, new EntitlementSettingsText(
                    "SecurityGroup", $"CN={key}-{tag},OU=Groups,DC=corp,DC=test", "Test", "Person", entitlementOwner, "grp_escalation", privileged, "Medium", types, mode, days, dflt, max), Ct);
            }

            await using (var o = Catalog(entitlementOwner, permissions))
            {
                await o.Entitlements.AcceptOwnershipAsync(e.Id, e.Draft!.ContentHash, Ct);
            }

            await using var c = Catalog(Admin, permissions);
            var proposed = await c.Entitlements.ProposeAsync(e.Id, "Ready", Ct);
            await using var k = Catalog(Checker, permissions);
            await k.Entitlements.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct);
            return e.Id;
        }

        var standard = await EntitlementAsync("readers", false, ["Primary"], "RequesterChooses", null, 30, 90);
        var privilegedId = await EntitlementAsync("admins", true, ["Secondary"], "FixedDays", 14, null, null);
        var serviceAllowed = await EntitlementAsync("reports", false, ["Service"], "NoExpiry", null, null, null);

        async Task<Guid> LinkAsync(string owner, string name)
        {
            await using var a = Accounts(Admin, permissions);
            var link = await a.Service.ProposeLinkAsync(app.Id, "Primary", name, "CN=" + name, owner, "Matches HR", Ct);
            await using var k = Accounts(Checker, permissions);
            return (await k.Service.ApproveLinkAsync(link.Id, null, Ct)).AccountId!.Value;
        }

        return new World(beneficiary, entitlementOwner, app.Id, standard, privilegedId, serviceAllowed, await LinkAsync(beneficiary, $"ben.{tag}"), await LinkAsync(other, $"other.{tag}"), $"ben.{tag}");
    }

    // ---- Matrix selections -------------------------------------------------------------------------------------------------

    private static MatrixRouteStage Stage(string source, int sequence = 1, string? workgroup = null, string kind = "Decision", string[]? fields = null) =>
        new(sequence, kind, source, null, workgroup, "AnyOne", null, false, false, 72, "Escalate", fields ?? []);

    private static MatrixRouteStage[] Route(params MatrixRouteStage[] stages) => stages;

    /// <summary>A matched selection with the given route, or (null) a hold.</summary>
    private sealed class FixedMatrix(IReadOnlyList<MatrixRouteStage>? route) : IApprovalMatrix
    {
        public const string Maker = "matrix-author";
        private static readonly Guid Version = Guid.CreateVersion7();

        public Task<MatrixSelection> SelectAsync(IReadOnlyList<MatrixFact> facts, DateTimeOffset asOfUtc, CancellationToken cancellationToken) =>
            Task.FromResult(route is null
                ? new MatrixSelection("HoldNoMatch", Version, 1, new string('c', 64), new string('d', 64), null, [], "{}", Maker)
                : new MatrixSelection(MatrixSelection.Matched, Version, 1, new string('c', 64), new string('d', 64), Guid.CreateVersion7(), route, "{\"evidence\":true}", Maker));
    }

    // ---- Services ------------------------------------------------------------------------------------------------------

    private RequestScope Service(string actor, IApprovalMatrix matrix)
    {
        var context = TestSupport.CreateRequest(db.Provider, db.RequestAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var catalog = TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString);
        var accounts = TestSupport.CreateAccount(db.Provider, db.AccountAppConnectionString);
        var (workflow, workflowContext) = Workflow();
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor), new AuditActionCatalog(RequestAuditActions.All.Select(a => new AuditActionRegistration(a))));
        var service = new RequestItemService(
            context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor),
            new Grants((actor, AccessActions.RequestItemsSubmit), (actor, AccessActions.RequestItemsNameAccount)),
            new EntitlementDirectory(catalog), new AccountDirectory(accounts), new AccountNaming(accounts), new UnavailablePersonAttributeSource(), matrix,
            new ApprovalStageChecks(workflow));
        return new RequestScope(service, context, catalog, accounts, workflowContext);
    }

    private CatalogScope Catalog(string actor, Grants permissions)
    {
        var context = TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor),
            new AuditActionCatalog(CatalogAuditActions.All.Concat(EntitlementAuditActions.All).Select(a => new AuditActionRegistration(a))));
        var workgroups = new AnyWorkgroup();
        return new CatalogScope(
            new ApplicationCatalogAdministrationService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions, workgroups),
            new EntitlementCatalogAdministrationService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions, workgroups),
            context);
    }

    private AccountScope Accounts(string actor, Grants permissions)
    {
        var context = TestSupport.CreateAccount(db.Provider, db.AccountAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var catalog = TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString);
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor), new AuditActionCatalog(AccountAuditActions.All.Select(a => new AuditActionRegistration(a))));
        return new AccountScope(
            new AccountAdministrationService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions, new ApplicationApproverDirectory(catalog), new NoAttributes()),
            context, catalog);
    }

    private (ApprovalWorkflowService Service, IAsyncDisposable Context) Workflow(string actor = "system:workflow", IRequestItemSource? items = null)
    {
        var context = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor),
            new AuditActionCatalog(WorkflowAuditActions.All.Select(a => new AuditActionRegistration(a))));
        var resolver = new ApproverResolver(
            new ApplicationApproverDirectory(TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString)),
            new WorkgroupDirectory(TestSupport.CreateOrganization(db.Provider, db.OrganizationAppConnectionString)),
            new ApproverResolutionSettings(),
            new EntitlementDirectory(TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString)));
        return (new ApprovalWorkflowService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), new Grants((actor, AccessActions.ApprovalsAct)), resolver,
            items ?? ItemSource()), context);
    }

    /// <summary>Frozen items read with the request login, for a completion to record the account name (DL-099); disposed with the test.</summary>
    private RequestItemSource ItemSource()
    {
        var request = TestSupport.CreateRequest(db.Provider, db.RequestAppConnectionString);
        _disposables.Add(request);
        return new RequestItemSource(request);
    }

    /// <summary>The name the item has now, as the person completing its level sees it and must confirm (DL-101).</summary>
    private async Task<string?> SeenNameAsync(Guid itemId) => (await ItemSource().CurrentAccountNameAsync(itemId, Ct))?.Name;

    private async Task<MessageEnvelope> OutboxEnvelopeAsync(Guid itemId)
    {
        await using var context = TestSupport.CreateRequest(db.Provider, db.RequestAppConnectionString);
        var row = await context.Outbox.AsNoTracking().SingleAsync(o => o.IdempotencyKey == $"request.item.submitted:{itemId:D}", Ct);
        return row.ToEnvelope();
    }

    private string Table(string name) => IsSqlServer ? $"[request].[{name}]" : $"request.{IamModelConventions.ToSnakeCase(name)}";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

    private async Task<long> AuditCountAsync(string? targetId, string action, string outcome)
    {
        var target = targetId is null ? string.Empty : IsSqlServer ? $" AND [TargetId] = '{targetId}'" : $" AND target_id = '{targetId}'";
        var sql = IsSqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [Action] = '{action}' AND [Outcome] = '{outcome}'{target}"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE action = '{action}' AND outcome = '{outcome}'{target}";
        return Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }

    private sealed class Grants(params (string Person, string Action)[] grants) : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) => Task.FromResult(grants.Contains((subject, action)));
    }

    private sealed class AnyWorkgroup : IWorkgroupDirectory
    {
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<WorkgroupMembership?> FindActiveAsync(string key, CancellationToken cancellationToken) => Task.FromResult<WorkgroupMembership?>(null);
    }

    private sealed class NoAttributes : IAttributeCatalog
    {
        public Task<PublishedAttributeDefinition?> FindPublishedByKeyAsync(string key, CancellationToken cancellationToken) => Task.FromResult<PublishedAttributeDefinition?>(null);

        public Task<PublishedAttributeDefinition?> FindPublishedByIdAsync(Guid attributeDefinitionId, CancellationToken cancellationToken) => Task.FromResult<PublishedAttributeDefinition?>(null);
    }

    private sealed class RequestScope(RequestItemService service, params IAsyncDisposable[] contexts) : IAsyncDisposable
    {
        public RequestItemService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            foreach (var c in contexts)
            {
                await c.DisposeAsync();
            }
        }
    }

    private sealed class CatalogScope(ApplicationCatalogAdministrationService applications, EntitlementCatalogAdministrationService entitlements, IAsyncDisposable context) : IAsyncDisposable
    {
        public ApplicationCatalogAdministrationService Applications { get; } = applications;

        public EntitlementCatalogAdministrationService Entitlements { get; } = entitlements;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class AccountScope(AccountAdministrationService service, params IAsyncDisposable[] contexts) : IAsyncDisposable
    {
        public AccountAdministrationService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            foreach (var c in contexts)
            {
                await c.DisposeAsync();
            }
        }
    }
}
