using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using IAM.Modules.Account.Infrastructure.Accounts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Infrastructure.Entitlements;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Provisioning.Infrastructure.Persistence;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Infrastructure.Items;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Workers.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 8 slice 5a (DL-093) against both engines: the R-037 check of an approval's log against the audit store, and the
/// provisioning consumer that plans connector operations only for a verified, authorised approval once its Action levels
/// are done, blocking with a reason anything it can't act on. Nothing is run against a target yet.
/// </summary>
public sealed partial class RequestItemParityTests
{
    private const string AccountContainer = "OU=Users,DC=corp,DC=test";

    [Fact]
    public async Task AVerifiedApprovalForAnExistingAccountIsPlannedOnce()
    {
        var world = await WorldAsync();
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));

        var report = await IntegrityAsync(item);
        report.Status.ShouldBe(ApprovalIntegrityStatus.Verified);
        report.ProvisioningAuthorized.ShouldBeTrue();
        report.ActionsOpen.ShouldBeFalse();
        report.Beneficiary.ShouldBe(world.Beneficiary);

        await using (var scope = Provisioning(Bound(world)))
        {
            (await scope.Handler.HandleAsync(await WorkflowEnvelopeAsync(item, ApprovalAuthorized.MessageType), Ct)).ShouldBe(MessageOutcome.Processed);
        }

        var (planned, operations) = await PlannedAsync(item);
        planned.State.ShouldBe(ProvisioningState.Planned);
        planned.ConnectorKey.ShouldBe("mock-ad");
        planned.ApprovalActions.ShouldBe(report.Actions);
        planned.AccountId.ShouldBe(world.Primary);
        operations.ShouldHaveSingleItem().Action.ShouldBe("account.entitlement.add");
        operations[0].TargetId.ShouldBe($"CN={world.PrimaryName}", "the linked account's target id");
        operations[0].RelatedId!.ShouldStartWith("CN=readers-", Case.Sensitive, "the entitlement's technical name is the group");
        operations[0].Id.ShouldBe(ProvisioningOperation.OperationId(item, 1));

        // Again, and the closed event too: one plan, one audit record.
        await using (var scope = Provisioning(Bound(world)))
        {
            (await scope.Handler.HandleAsync(await WorkflowEnvelopeAsync(item, ApprovalAuthorized.MessageType), Ct)).ShouldBe(MessageOutcome.Duplicate);
            (await scope.Handler.HandleAsync(await WorkflowEnvelopeAsync(item, ApprovalClosed.MessageType), Ct)).ShouldBe(MessageOutcome.Duplicate);
        }

        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.Planned, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task ANewAccountWaitsForItsNameLevelThenIsPlannedWithTheGivenName()
    {
        var world = await WorldAsync();
        var name = $"adm.{Guid.NewGuid():N}"[..20];

        // The owner decides, then names the account on an Action level after the decision.
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);

        // Authorised by the decision, but the name level is open: nothing is planned yet, and nothing is blocked.
        var waiting = await IntegrityAsync(item);
        waiting.ProvisioningAuthorized.ShouldBeTrue();
        waiting.ActionsOpen.ShouldBeTrue();
        await using (var scope = Provisioning(Bound(world)))
        {
            (await scope.Handler.HandleAsync(await WorkflowEnvelopeAsync(item, ApprovalAuthorized.MessageType), Ct)).ShouldBe(MessageOutcome.Processed);
        }

        (await FindPlannedAsync(item)).ShouldBeNull();

        await NameAndCompleteAsync(world, item, route, name);
        await using (var scope = Provisioning(Bound(world)))
        {
            (await scope.Handler.HandleAsync(await WorkflowEnvelopeAsync(item, ApprovalClosed.MessageType), Ct)).ShouldBe(MessageOutcome.Processed);
        }

        var (planned, operations) = await PlannedAsync(item);
        planned.State.ShouldBe(ProvisioningState.Planned);
        planned.AccountName.ShouldBe(name, "the name the level gave");
        planned.NewAccountType.ShouldBe("Secondary");
        operations.Select(o => o.Action).ShouldBe(["account.create", "account.entitlement.add", "account.enable"]);
        operations.ShouldAllBe(o => o.TargetId == $"CN={name},{AccountContainer}");
    }

    [Fact]
    public async Task WithoutANameOrAConnectorNothingIsPlannedAndThePersonIsTold()
    {
        var world = await WorldAsync();

        // The name level was completed without a name (DL-092): blocked, not created nameless.
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var nameless = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        await NameAndCompleteAsync(world, nameless, route, name: null);
        await using (var scope = Provisioning(Bound(world)))
        {
            (await scope.Handler.HandleAsync(await WorkflowEnvelopeAsync(nameless, ApprovalClosed.MessageType), Ct)).ShouldBe(MessageOutcome.Processed);
        }

        (await PlannedAsync(nameless)).Item.Problem.ShouldBe("NoAccountName");

        // No connector serves the application: manual work.
        var manual = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));
        await using (var scope = Provisioning())
        {
            (await scope.Handler.HandleAsync(await WorkflowEnvelopeAsync(manual, ApprovalAuthorized.MessageType), Ct)).ShouldBe(MessageOutcome.Processed);
        }

        var (blocked, operations) = await PlannedAsync(manual);
        blocked.State.ShouldBe(ProvisioningState.Blocked);
        blocked.Problem.ShouldBe("ManualWork");
        blocked.ConnectorKey.ShouldBeNull();
        operations.ShouldBeEmpty();
        (await AuditCountAsync(manual.ToString(), ProvisioningAuditActions.Blocked, "Denied")).ShouldBe(1);
    }

    [Fact]
    public async Task AnAuthorisationTheAuditDoesNotBackIsBlocked()
    {
        var world = await WorldAsync();
        var route = new FixedMatrix(Route(Stage("EntitlementOwner")));

        // An approval that doesn't exist.
        var unknown = Guid.CreateVersion7();
        (await HandleAsync(world, Authorized(unknown, 3))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(unknown)).Item.ShouldSatisfyAllConditions(i => i.Problem.ShouldBe("Integrity"), i => i.IntegrityProblem.ShouldBe("NotFound"), i => i.ApprovalActions.ShouldBeNull());

        // A message that claims more than the log holds.
        var ahead = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));
        (await HandleAsync(world, Authorized(ahead, 99))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(ahead)).Item.IntegrityProblem.ShouldBe("MessageAhead");

        // A verified approval that hasn't authorised anything: the "authorisation" isn't one.
        RequestItemView started;
        await using (var scope = Service(world.Beneficiary, route))
        {
            started = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.ServiceAllowed, null, "Service", $"svc.{Guid.NewGuid():N}"[..16], null), Ct);
        }

        await StartAsync(started.Id);
        (await HandleAsync(world, Authorized(started.Id, 1))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(started.Id)).Item.Problem.ShouldBe("NotAuthorized");

        // A forged second audit record of an action: the stores disagree.
        // (One waiting item per person and entitlement: each case below has its own world.)
        var other = await WorldAsync();
        var duplicated = await ApprovedAsync(other, new SubmitItem(other.Beneficiary, other.Standard, other.Primary, null, null, null));
        await ForgeAuditAsync(duplicated, sequence: 2);
        (await IntegrityAsync(duplicated)).ShouldSatisfyAllConditions(r => r.Status.ShouldBe(ApprovalIntegrityStatus.Mismatch), r => r.Problem.ShouldBe("AuditDuplicate"));
        (await HandleAsync(other, await WorkflowEnvelopeAsync(duplicated, ApprovalAuthorized.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(duplicated)).Item.IntegrityProblem.ShouldBe("AuditDuplicate");

        // A forged action appended to the log by SQL with the workflow login: the log no longer replays.
        var third = await WorldAsync();
        var forged = await ApprovedAsync(third, new SubmitItem(third.Beneficiary, third.Standard, third.Primary, null, null, null));
        var count = (await IntegrityAsync(forged)).Actions;
        var wf = IsSqlServer ? "[workflow].[ApprovalAction]" : "workflow.approval_action";
        await db.ExecuteAsync(db.WorkflowAppConnectionString, IsSqlServer
            ? $"INSERT INTO {wf} ([InstanceId], [Sequence], [Kind], [Payload], [AtUtc], [Actor], [VersionAfter]) VALUES ('{forged}', {count + 1}, 'Decided', '{{}}', SYSUTCDATETIME(), 'mallory', 99)"
            : $"INSERT INTO {wf} (instance_id, sequence, kind, payload, at_utc, actor, version_after) VALUES ('{forged}', {count + 1}, 'Decided', '{{}}', now(), 'mallory', 99)", Ct);
        (await IntegrityAsync(forged)).Status.ShouldBe(ApprovalIntegrityStatus.Damaged);
        (await HandleAsync(third, await WorkflowEnvelopeAsync(forged, ApprovalAuthorized.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(forged)).Item.IntegrityProblem.ShouldBe("DoesNotReplay");

        foreach (var id in new[] { unknown, ahead, started.Id, duplicated, forged })
        {
            (await AuditCountAsync(id.ToString(), ProvisioningAuditActions.Planned, "Succeeded")).ShouldBe(0, "nothing planned");
            (await AuditCountAsync(id.ToString(), ProvisioningAuditActions.Blocked, "Denied")).ShouldBe(1);
        }
    }

    [Fact]
    public async Task ThePlanAndTheLedgerCannotBeRewrittenAndStatesOnlyMoveForward()
    {
        var world = await WorldAsync();
        var planned = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));
        await HandleAsync(world, await WorkflowEnvelopeAsync(planned, ApprovalAuthorized.MessageType), Bound(world));
        var blocked = Guid.CreateVersion7();
        await HandleAsync(world, Authorized(blocked, 1));
        var operation = ProvisioningOperation.OperationId(planned, 1);

        await using (var scope = new ServiceScopeLedger(db))
        {
            await scope.Ledger.RecordAsync(OperationLedgerRecord.Create(operation, new string('a', 64), "Applied", null, DateTimeOffset.UtcNow), Ct);
        }

        string Item(string set, Guid id) => $"UPDATE {P("ProvisioningItem")} SET {set} WHERE {C("Id")} = '{id}'";
        string Op(string set) => $"UPDATE {P("ProvisioningOperation")} SET {set} WHERE {C("Id")} = '{operation}'";
        foreach (var login in new[] { db.ProvisioningAppConnectionString, db.MigratorConnectionString })
        {
            foreach (var sql in new[]
            {
                Item($"{C("AccountName")} = 'mallory'", planned),
                Item($"{C("ConnectorKey")} = 'other'", planned),
                Item($"{C("EntitlementId")} = '{Guid.NewGuid()}'", planned),
                Item($"{C("State")} = 'Succeeded'", planned),
                Item($"{C("CorrelationId")} = 'ffffffffffffffffffffffffffffffff'", planned),
                Item($"{C("Attempts")} = -1", planned),
                Item($"{C("RecordedAccountId")} = '{Guid.NewGuid()}'", planned),
                Item($"{C("State")} = 'Planned', {C("Problem")} = NULL, {C("ConnectorKey")} = 'mock-ad'", blocked),
                Op($"{C("TargetId")} = 'CN=mallory'"),
                Op($"{C("RelatedId")} = 'CN=Domain Admins'"),
                Op($"{C("State")} = 'Failed'"),
                $"DELETE FROM {P("ProvisioningOperation")} WHERE {C("Id")} = '{operation}'",
                $"DELETE FROM {P("ProvisioningItem")} WHERE {C("Id")} = '{blocked}'",
                $"UPDATE {P("OperationLedger")} SET {C("Status")} = 'Failed' WHERE {C("OperationId")} = '{operation}'",
                $"DELETE FROM {P("OperationLedger")} WHERE {C("OperationId")} = '{operation}'",
                $"UPDATE {P("InboxMessage")} SET {C("Type")} = 'x.y' WHERE {C("ConsumerName")} = 'provisioning.approvals'",
                $"DELETE FROM {P("InboxMessage")} WHERE {C("ConsumerName")} = 'provisioning.approvals'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldNotBeNullOrEmpty();
            }
        }

        // Forward only: Planned to Running, back to Planned for a retry, Running to Succeeded; never back from Succeeded.
        foreach (var state in new[] { "Running", "Planned", "Running" })
        {
            await db.ExecuteAsync(db.ProvisioningAppConnectionString, Item($"{C("State")} = '{state}'", planned), Ct);
        }

        // Succeeded only with its finishing time (DL-094).
        await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.ProvisioningAppConnectionString, Item($"{C("State")} = 'Succeeded'", planned), Ct));
        await db.ExecuteAsync(db.ProvisioningAppConnectionString, Item($"{C("State")} = 'Succeeded', {C("FinishedAtUtc")} = {(IsSqlServer ? "SYSUTCDATETIME()" : "now()")}", planned), Ct);

        await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.ProvisioningAppConnectionString, Item($"{C("State")} = 'Running'", planned), Ct));
        await db.ExecuteAsync(db.ProvisioningAppConnectionString, Op($"{C("State")} = 'Running'"), Ct);
        await db.ExecuteAsync(db.ProvisioningAppConnectionString, Op($"{C("State")} = 'Failed'"), Ct);
        await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.ProvisioningAppConnectionString, Op($"{C("State")} = 'Planned'"), Ct));

        if (!IsSqlServer)
        {
            foreach (var tables in new[] { "provisioning.provisioning_item, provisioning.provisioning_operation", "provisioning.provisioning_operation", "provisioning.operation_ledger", "provisioning.inbox_message" })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE {tables}", Ct), tables)).Message.ShouldMatch("(?i)DL-0(78|93)");
            }
        }
    }

    [Fact]
    public async Task AConnectorThatRestartsStillKnowsWhatItFinished()
    {
        var operation = new ConnectorOperation(
            Guid.CreateVersion7(),
            ActionCatalog.AccountCreate,
            new ObjectRef(ObjectTypes.Account, $"CN=ledger-{Guid.NewGuid():N},OU=Users,{MockActiveDirectoryConnector.DefaultBaseDn}"),
            new ObjectRef(ObjectTypes.Container, $"OU=Users,{MockActiveDirectoryConnector.DefaultBaseDn}"),
            new Dictionary<string, string?> { ["sAMAccountName"] = "ledger" },
            null);
        var services = new ServiceCollection()
            .AddSingleton(TestSupport.ProviderFor(db.Provider))
            .AddScoped(_ => TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString))
            .AddScoped<IProvisioningLedger>(sp => new ProvisioningLedgerStore(sp.GetRequiredService<ProvisioningDbContext>(), sp.GetRequiredService<IIamDatabaseProvider>()))
            .BuildServiceProvider();
        await using var _ = services;
        var ledger = new DurableOperationLedger(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        var first = new MockActiveDirectoryConnector(ConnectorSettings.Default, ledger);
        first.AddContainer("Users");
        (await first.ExecuteAsync(operation, Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await first.ReadAsync(operation.Target, Ct)).ShouldNotBeNull();

        // A new connector process (the mock's directory starts empty): the same operation is recognised and not made again.
        var restarted = new MockActiveDirectoryConnector(ConnectorSettings.Default, ledger);
        restarted.AddContainer("Users");
        (await restarted.ExecuteAsync(operation, Ct)).Status.ShouldBe(OperationStatus.Applied, "what happened the first time");
        (await restarted.ReadAsync(operation.Target, Ct)).ShouldBeNull("nothing was created twice");
        (await restarted.ExecuteAsync(operation with { Attributes = new Dictionary<string, string?> { ["sAMAccountName"] = "other" } }, Ct))
            .ShouldSatisfyAllConditions(r => r.Status.ShouldBe(OperationStatus.Rejected), r => r.Code.ShouldBe(ResultCodes.OperationIdReused));

        // The first record stands.
        await ledger.RecordAsync(new LedgerEntry(operation.OperationId, new string('b', 64), new OperationResult(operation.OperationId, OperationStatus.Rejected, "X")), Ct);
        (await ledger.FindAsync(operation.OperationId, Ct))!.Result.Status.ShouldBe(OperationStatus.Applied);
    }

    // ---- Provisioning helpers ----------------------------------------------------------------------------------------------

    private static ConnectorBinding[] Bound(World world) => [ConnectorBinding.Create(world.Application, "mock-ad", AccountContainer)];

    private static MessageEnvelope Authorized(Guid id, int sequence) => MessageEnvelopes.Create(
        new ApprovalAuthorized(id, id, sequence, 1), WorkflowEvents.Source, Guid.NewGuid().ToString("N"), null, $"test.authorized:{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    /// <summary>Submits, starts and has the entitlement owner approve every decision level; Action levels are left open.</summary>
    private async Task<Guid> ApprovedAsync(World world, SubmitItem submit, FixedMatrix? route = null)
    {
        route ??= new FixedMatrix(Route(Stage("EntitlementOwner")));
        RequestItemView item;
        await using (var scope = Service(world.Beneficiary, route))
        {
            item = await scope.Service.SubmitAsync(submit, Ct);
        }

        await StartAsync(item.Id);
        var (workflow, context) = Workflow(world.EntitlementOwner);
        await using (context)
        {
            var instance = (await workflow.FindAsync(item.Id, Ct)).ShouldNotBeNull();
            foreach (var stage in instance.Stages.Where(s => s is { Kind: "Decision", State: "Active" }))
            {
                instance = await workflow.DecideAsync(item.Id, instance.Version, Guid.CreateVersion7(), stage.StageId, true, null, Ct);
            }
        }

        return item.Id;
    }

    private async Task StartAsync(Guid itemId)
    {
        var (workflow, context) = Workflow();
        await using (context)
        {
            (await new RequestItemApprovals(workflow).HandleAsync(await OutboxEnvelopeAsync(itemId), Ct)).ShouldBe(MessageOutcome.Processed);
        }
    }

    /// <summary>The owner gives the name (unless null) on the open Action level, then completes it; the approval closes.</summary>
    private async Task NameAndCompleteAsync(World world, Guid itemId, FixedMatrix route, string? name)
    {
        var (workflow, context) = Workflow(world.EntitlementOwner);
        await using (context)
        {
            var instance = (await workflow.FindAsync(itemId, Ct)).ShouldNotBeNull();
            var stage = instance.Stages.Single(s => s is { Kind: "Action", State: "Active" });
            if (name is not null)
            {
                await using var scope = Service(world.EntitlementOwner, route);
                (await scope.Service.GiveAccountNameAsync(itemId, stage.StageId, name, Ct)).AccountName.ShouldBe(name);
            }

            (await workflow.CompleteAsync(itemId, instance.Version, Guid.CreateVersion7(), stage.StageId, "Done", Ct)).State.ShouldBe("Approved");
        }
    }

    private async Task<ApprovalIntegrityReport> IntegrityAsync(Guid instanceId)
    {
        await using var workflow = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString);
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        return await new ApprovalIntegrityCheck(workflow, new AuditTrail(audit)).CheckAsync(instanceId, Ct);
    }

    /// <summary>What a forger with the workflow login could add: a second audit record of an action, with a made-up hash.</summary>
    private async Task ForgeAuditAsync(Guid instanceId, int sequence)
    {
        await using var context = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString);
        await using var tx = await context.Database.BeginTransactionAsync(Ct);
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor("mallory"),
            new AuditActionCatalog(WorkflowAuditActions.All.Select(a => new AuditActionRegistration(a))));
        await writer.AppendAsync(tx.GetDbTransaction(), new AuditEventRecord
        {
            Action = "workflow.approval.decided",
            TargetType = WorkflowAuditActions.TargetType,
            TargetId = instanceId.ToString(),
            Outcome = AuditOutcome.Succeeded,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Payload =
            [
                new("kind", "Decided", AuditFieldClass.Plain),
                new("sequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditFieldClass.Plain),
                new("versionAfter", "3", AuditFieldClass.Plain),
                new("actionHash", new string('0', 64), AuditFieldClass.Plain),
            ],
        }, Ct);
        await tx.CommitAsync(Ct);
    }

    private async Task<MessageEnvelope> WorkflowEnvelopeAsync(Guid itemId, string type)
    {
        await using var context = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString);
        var text = itemId.ToString("D");
        var rows = await context.Outbox.AsNoTracking().Where(o => o.Type == type && o.Payload.Contains(text)).ToListAsync(Ct);
        return rows.Select(r => r.ToEnvelope()).Single(e => (type == ApprovalClosed.MessageType
            ? MessageEnvelopes.Read<ApprovalClosed>(e).InstanceId
            : MessageEnvelopes.Read<ApprovalAuthorized>(e).InstanceId) == itemId);
    }

    private async Task<MessageOutcome> HandleAsync(World world, MessageEnvelope envelope, params ConnectorBinding[] bindings)
    {
        await using var scope = Provisioning(bindings.Length > 0 ? bindings : Bound(world));
        return await scope.Handler.HandleAsync(envelope, Ct);
    }

    private async Task<ProvisioningItem?> FindPlannedAsync(Guid itemId)
    {
        await using var context = TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString);
        return await context.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, Ct);
    }

    private async Task<(ProvisioningItem Item, List<ProvisioningOperation> Operations)> PlannedAsync(Guid itemId)
    {
        await using var context = TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString);
        var item = (await context.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, Ct)).ShouldNotBeNull();
        return (item, await context.Operations.AsNoTracking().Where(o => o.ItemId == itemId).OrderBy(o => o.Step).ToListAsync(Ct));
    }

    private ProvisioningScope Provisioning(params ConnectorBinding[] bindings)
    {
        const string actor = "system:provisioning";
        var context = TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var workflow = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString);
        var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        var request = TestSupport.CreateRequest(db.Provider, db.RequestAppConnectionString);
        var catalog = TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString);
        var accounts = TestSupport.CreateAccount(db.Provider, db.AccountAppConnectionString);
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor),
            new AuditActionCatalog(ProvisioningAuditActions.All.Select(a => new AuditActionRegistration(a))));
        var handler = new AuthorizedApprovals(
            context,
            TestSupport.ProviderFor(db.Provider),
            TimeProvider.System,
            writer,
            new ApprovalIntegrityCheck(workflow, new AuditTrail(audit)),
            new RequestItemSource(request),
            new EntitlementDirectory(catalog),
            new AccountDirectory(accounts),
            new ConnectorBindings(bindings));
        return new ProvisioningScope(handler, context, workflow, audit, request, catalog, accounts);
    }

    private string P(string table) => IsSqlServer ? $"[provisioning].[{table}]" : $"provisioning.{IamModelConventions.ToSnakeCase(table)}";

    private string C(string column) => Col(column);

    private sealed class ProvisioningScope(AuthorizedApprovals handler, params IAsyncDisposable[] contexts) : IAsyncDisposable, IDisposable
    {
        public AuthorizedApprovals Handler => handler;

        public void Dispose()
        {
            foreach (var context in contexts.OfType<IDisposable>())
            {
                context.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    private sealed class ServiceScopeLedger(ParityDatabase db) : IAsyncDisposable
    {
        private readonly ProvisioningDbContext _context = TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString);

        public ProvisioningLedgerStore Ledger => new(_context, TestSupport.ProviderFor(db.Provider));

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }
}
