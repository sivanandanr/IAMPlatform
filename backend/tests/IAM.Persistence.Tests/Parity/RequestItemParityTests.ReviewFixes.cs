using IAM.BuildingBlocks.Messaging;
using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Infrastructure.Items;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Workers.Provisioning;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 1 (DL-096), the stage 8 review's High findings, on both engines: a message can't choose an approval's
/// route; the runner runs only the plan the verified approval still gives; a create never takes over an object that was
/// already there; and the account rules are checked before the target changes, with access taken away again if a new
/// account can't be recorded after all.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task AMessageCanNotChooseAnApprovalsRoute()
    {
        var world = await WorldAsync();
        RequestItemView item;
        await using (var scope = Service(world.Beneficiary, new FixedMatrix(Route(Stage("EntitlementOwner")))))
        {
            item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null), Ct);
        }

        var frozen = await FrozenAsync(item.Id);
        var accomplices = frozen with { Stages = [frozen.Stages[0] with { Source = "Workgroup", WorkgroupKey = "accomplices", EntitlementId = null }] };
        var (workflow, context) = Workflow();
        await using (context)
        {
            // Another route for a real item, and any route for no item: refused, never started.
            await Should.ThrowAsync<InvalidMessageException>(() => Starter(workflow).HandleAsync(Submitted(accomplices), Ct));
            await Should.ThrowAsync<InvalidMessageException>(() => Starter(workflow).HandleAsync(Submitted(frozen with { ItemId = Guid.CreateVersion7() }), Ct));
            (await workflow.FindAsync(item.Id, Ct)).ShouldBeNull();

            // What the item was frozen with starts it.
            (await Starter(workflow).HandleAsync(Submitted(frozen), Ct)).ShouldBe(MessageOutcome.Processed);
            (await workflow.FindAsync(item.Id, Ct)).ShouldNotBeNull().Stages.Single().Candidates.ShouldBe([world.EntitlementOwner]);
        }
    }

    [Fact]
    public async Task AnApprovalOnAnyOtherRouteGrantsNothing()
    {
        // An approval started some other way, on a route the matrix didn't give (here a shorter deadline): it replays and its
        // audit matches, but it isn't the item's route.
        var world = await WorldAsync();
        RequestItemView item;
        await using (var scope = Service(world.Beneficiary, new FixedMatrix(Route(Stage("EntitlementOwner")))))
        {
            item = await scope.Service.SubmitAsync(new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null), Ct);
        }

        var frozen = await FrozenAsync(item.Id);
        var (binding, route) = RequestItemApprovals.Build(frozen with { Stages = [frozen.Stages[0] with { DeadlineHours = 1 }] });
        var (workflow, context) = Workflow();
        await using (context)
        {
            await workflow.StartAsync(binding, route, Ct);
        }

        var (owner, ownerContext) = Workflow(world.EntitlementOwner);
        await using (ownerContext)
        {
            var instance = (await owner.FindAsync(item.Id, Ct)).ShouldNotBeNull();
            await owner.DecideAsync(item.Id, instance.Version, Guid.CreateVersion7(), instance.Stages.Single().StageId, true, null, Ct);
        }

        (await IntegrityAsync(item.Id)).Status.ShouldBe(ApprovalIntegrityStatus.Verified);
        (await HandleAsync(world, await WorkflowEnvelopeAsync(item.Id, ApprovalAuthorized.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item.Id)).Item.ShouldSatisfyAllConditions(i => i.Problem.ShouldBe("Integrity"), i => i.IntegrityProblem.ShouldBe("RouteMismatch"));
    }

    [Fact]
    public async Task TheRunnerRunsOnlyThePlanTheApprovalStillGives()
    {
        // A step added to a planned item by SQL with the provisioning login: the runner decides again and stops.
        var item = await PlannedExistingAsync();
        var table = IsSqlServer ? "[provisioning].[ProvisioningOperation]" : "provisioning.provisioning_operation";
        var injected = ProvisioningOperation.OperationId(item, 2);
        await db.ExecuteAsync(db.ProvisioningAppConnectionString, IsSqlServer
            ? $"INSERT INTO {table} ([Id], [ItemId], [Step], [Action], [TargetType], [TargetId], [RelatedType], [RelatedId], [Attributes], [State], [CreatedAtUtc], [CreatedBy], [ModifiedAtUtc], [ModifiedBy], [Version]) VALUES ('{injected}', '{item}', 2, 'account.entitlement.add', 'account', 'CN=victim,OU=Users,DC=corp,DC=test', 'entitlement', 'CN=Domain Admins,OU=Groups,DC=corp,DC=test', '{{}}', 'Planned', SYSUTCDATETIME(), 'mallory', SYSUTCDATETIME(), 'mallory', 1)"
            : $"INSERT INTO {table} (id, item_id, step, action, target_type, target_id, related_type, related_id, attributes, state, created_at_utc, created_by, modified_at_utc, modified_by, version) VALUES ('{injected}', '{item}', 2, 'account.entitlement.add', 'account', 'CN=victim,OU=Users,DC=corp,DC=test', 'entitlement', 'CN=Domain Admins,OU=Groups,DC=corp,DC=test', '{{}}', 'Planned', now(), 'mallory', now(), 'mallory', 1)", Ct);

        var target = new ScriptedTarget(StepStatus.Done);
        (await RunAsync(item, target)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("PlanMismatch"));
        target.Runs.ShouldBe(0, "nothing ran");
    }

    [Fact]
    public async Task ACreateNeverTakesOverAnAccountThatWasAlreadyThere()
    {
        var (item, name, mock, group) = await PlannedNewAccountAsync();

        // Someone else's account with that DN and login already exists in the directory.
        var dn = $"CN={name},{AccountContainer}";
        (await mock.ExecuteAsync(new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.AccountCreate, new ObjectRef(ObjectTypes.Account, dn),
            new ObjectRef(ObjectTypes.Container, AccountContainer), new Dictionary<string, string?> { ["sAMAccountName"] = name }, null), Ct)).Status.ShouldBe(OperationStatus.Applied);

        // The create's call breaks off: reading the target would find "an" account, which proves nothing (DL-096). It is retried.
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var broken = new HookedTarget(new ConnectorTarget(mock), step => step.Action == "account.create" ? throw new TimeoutException("The call broke off.") : Task.CompletedTask);
        (await RunAsync(item, broken, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).ShouldSatisfyAllConditions(
            p => p.Item.State.ShouldBe(ProvisioningState.Planned, "not done on a read"),
            p => p.Operations[0].State.ShouldBe(ProvisioningState.Planned));

        // Run again under the same operation id: the connector refuses to take the object over, and a person looks at it.
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, new ConnectorTarget(mock), clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBeOneOf("AlreadyExists", "TargetExists"));
        var existing = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, dn), Ct)).ShouldNotBeNull();
        existing.Links.ShouldNotContain(l => string.Equals(l.Id, group, StringComparison.OrdinalIgnoreCase), "nothing was granted to it");
        existing.Enabled.ShouldBe(false, "nor was it enabled");
    }

    [Fact]
    public async Task ANewAccountThatCanNotBeRecordedLosesItsAccessAgain()
    {
        var (item, name, mock, group) = await PlannedNewAccountAsync();
        var world = await WorldOfAsync(item);

        // While the steps run, someone links another account with that name: recording refuses after the target changed.
        var target = new HookedTarget(new ConnectorTarget(mock), async step =>
        {
            if (step.Action == "account.enable")
            {
                await LinkAsync(world.Application, name.ToUpperInvariant(), $"CN={name}-other,{AccountContainer}");
            }
        });
        (await RunAsync(item, target)).ShouldBeTrue();

        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("AccountNotRecorded"), i => i.RecordedAccountId.ShouldBeNull());
        steps.Where(s => s.Step > 10).Select(s => (s.Action, s.State)).ShouldBe([("account.entitlement.remove", ProvisioningState.Succeeded), ("account.disable", ProvisioningState.Succeeded)]);
        var account = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull();
        account.Enabled.ShouldBe(false);
        account.Links.ShouldNotContain(l => string.Equals(l.Id, group, StringComparison.OrdinalIgnoreCase));
        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.StepUndone, "Succeeded")).ShouldBe(2);
    }

    [Fact]
    public async Task ATakenTargetIdBlocksTheNewAccountBeforeAnythingRuns()
    {
        var world = await WorldAsync();
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        await NameAndCompleteAsync(world, item, route, name);

        // Another name, but the DN the new account would get is already someone's.
        await LinkAsync(world.Application, $"x{name}", $"CN={name},{AccountContainer}");
        (await HandleAsync(world, await WorkflowEnvelopeAsync(item, ApprovalClosed.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.Problem.ShouldBe("AccountTaken");
    }

    // ---- Review-fix helpers ------------------------------------------------------------------------------------------------

    private async Task<RequestItemSubmitted> FrozenAsync(Guid itemId)
    {
        await using var request = TestSupport.CreateRequest(db.Provider, db.RequestAppConnectionString);
        return (await new RequestItemSource(request).FindSubmittedAsync(itemId, Ct)).ShouldNotBeNull();
    }

    private static MessageEnvelope Submitted(RequestItemSubmitted item) =>
        MessageEnvelopes.Create(item, RequestEvents.Source, Guid.NewGuid().ToString("N"), null, $"test.submitted:{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    /// <summary>A planned new Secondary account, with a mock directory that has its container and group.</summary>
    private async Task<(Guid Item, string Name, MockActiveDirectoryConnector Mock, string Group)> PlannedNewAccountAsync()
    {
        var world = await WorldAsync();
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        await NameAndCompleteAsync(world, item, route, name);
        (await HandleAsync(world, await WorkflowEnvelopeAsync(item, ApprovalClosed.MessageType))).ShouldBe(MessageOutcome.Processed);
        var (planned, operations) = await PlannedAsync(item);
        planned.State.ShouldBe(ProvisioningState.Planned);

        var mock = new MockActiveDirectoryConnector(ConnectorSettings.Default, new InMemoryOperationLedger(), "DC=corp,DC=test");
        mock.AddContainer("Users");
        var group = operations[1].RelatedId.ShouldNotBeNull();
        mock.AddGroup(group["CN=".Length..group.IndexOf(',', StringComparison.Ordinal)], mock.AddContainer("Groups"));
        return (item, name, mock, group);
    }

    private async Task<(Guid Application, string Beneficiary)> WorldOfAsync(Guid item)
    {
        var (planned, _) = await PlannedAsync(item);
        return (planned.ApplicationId!.Value, planned.Beneficiary!);
    }

    /// <summary>Links an existing account to someone else, as two administrators would.</summary>
    private async Task LinkAsync(Guid application, string name, string targetId)
    {
        var permissions = new Grants((Admin, AccessActions.AccountsManage), (Checker, AccessActions.AccountsApprove));
        await using var a = Accounts(Admin, permissions);
        var link = await a.Service.ProposeLinkAsync(application, "Secondary", name, targetId, $"x-{Guid.NewGuid():N}"[..12], "Found in AD", Ct);
        await using var k = Accounts(Checker, permissions);
        await k.Service.ApproveLinkAsync(link.Id, null, Ct);
    }

    /// <summary>A target that runs something of the test's own just before each step.</summary>
    private sealed class HookedTarget(IProvisioningTarget inner, Func<TargetStep, Task> before) : IProvisioningTarget
    {
        public async Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            await before(operation);
            return await inner.RunAsync(operation, cancellationToken);
        }

        public Task<bool> VerifyAsync(TargetStep operation, CancellationToken cancellationToken) => inner.VerifyAsync(operation, cancellationToken);

        public Task<bool> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => inner.ExistsAsync(objectType, id, cancellationToken);
    }
}
