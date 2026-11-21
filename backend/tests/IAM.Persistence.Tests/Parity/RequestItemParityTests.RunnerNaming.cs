using IAM.BuildingBlocks.Messaging;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Workflow.Contracts;
using IAM.Workers.Provisioning;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 2 (DL-097), the stage 8 review's runner and naming findings, on both engines: a call to the target has a time
/// limit; the lease is renewed with each step so a long run isn't taken over; a run that keeps stopping ends as RunnerError;
/// a name committed after its Action level closed is not the one provisioned; and naming works while an authorised approval
/// is held.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task ACallThatTakesTooLongHasAnUnknownOutcomeAndIsRetried()
    {
        var item = await PlannedExistingAsync();
        var slow = new ScriptedTarget(StepStatus.Done) { Delay = TimeSpan.FromSeconds(30), Matches = false };
        var started = DateTime.UtcNow;
        (await RunAsync(item, slow, stepTimeoutSeconds: 1)).ShouldBeTrue();
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(25), "the call was cut off at its time limit");
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Planned), i => i.Attempts.ShouldBe(1));
    }

    [Fact]
    public async Task TheLeaseIsRenewedWithEachStepSoALongRunIsNotTakenOver()
    {
        var (item, _, _, _) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        bool? secondRunner = null;
        var steps = 0;
        var target = new HookedTarget(new ScriptedTarget(StepStatus.Done), async _ =>
        {
            steps++;
            if (steps == 1)
            {
                // The first step takes longer than the whole lease.
                clock.Advance(TimeSpan.FromMinutes(5));
            }
            else if (steps == 2)
            {
                // The lease was renewed when this step started: another runner can't take the item over.
                secondRunner = await RunAsync(item, new ScriptedTarget(StepStatus.Done), clock);
            }
        });

        (await RunAsync(item, target, clock)).ShouldBeTrue();
        secondRunner.ShouldBe(false);
        (await PlannedAsync(item)).Item.State.ShouldNotBe(ProvisioningState.Planned);
    }

    [Fact]
    public async Task ARunThatKeepsStoppingEndsAsARunnerError()
    {
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var broken = new BrokenTargets();

        // Each run stops with an error the runner can't handle; the item stays running until its lease runs out.
        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(item, null, clock, maxAttempts: 2, targets: broken));
        clock.Advance(TimeSpan.FromMinutes(2));
        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(item, null, clock, maxAttempts: 2, targets: broken));
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Running), i => i.Attempts.ShouldBe(1));

        // Taking it over again reaches the limit: it fails for a person instead of being retried for ever.
        clock.Advance(TimeSpan.FromMinutes(2));
        (await RunAsync(item, null, clock, maxAttempts: 2, targets: broken)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("RunnerError"));
        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.Failed, "Failed")).ShouldBe(1);
    }

    [Fact]
    public async Task ANameCommittedAfterItsLevelClosedIsNotTheOneProvisioned()
    {
        var world = await WorldAsync();
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        await NameAndCompleteAsync(world, item, route, name);

        // A name that committed just after the level closed (the race the stage check can't stop): the request login writes it.
        var stage = (await FrozenAsync(item)).Stages.Single(s => s.Kind == "Action").StageId;
        var table = IsSqlServer ? "[request].[AccountNameAssignment]" : "request.account_name_assignment";
        await db.ExecuteAsync(db.RequestAppConnectionString, IsSqlServer
            ? $"INSERT INTO {table} ([Id], [ItemId], [StageId], [PreviousName], [Name], [SetBy], [SetAtUtc], [CreatedAtUtc], [CreatedBy], [ModifiedAtUtc], [ModifiedBy], [Version]) VALUES ('{Guid.CreateVersion7()}', '{item}', '{stage}', '{name}', 'late.name', '{world.EntitlementOwner}', DATEADD(minute, 5, SYSUTCDATETIME()), SYSUTCDATETIME(), 'x', SYSUTCDATETIME(), 'x', 1)"
            : $"INSERT INTO {table} (id, item_id, stage_id, previous_name, name, set_by, set_at_utc, created_at_utc, created_by, modified_at_utc, modified_by, version) VALUES ('{Guid.CreateVersion7()}', '{item}', '{stage}', '{name}', 'late.name', '{world.EntitlementOwner}', now() + interval '5 minutes', now(), 'x', now(), 'x', 1)", Ct);

        (await HandleAsync(world, await WorkflowEnvelopeAsync(item, ApprovalClosed.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.AccountName.ShouldBe(name, "the name given while the level was open");
    }

    [Fact]
    public async Task ANameCanBeGivenWhileAnAuthorisedApprovalIsHeld()
    {
        // After the decision, two Action levels open together: the name level, and one with nobody to do it, which holds the
        // approval. The workflow still offers the name task, so the name must be accepted.
        var world = await WorldAsync();
        var route = new FixedMatrix(Route(
            Stage("EntitlementOwner"),
            Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"]),
            Stage("Workgroup", sequence: 2, workgroup: "no_such_workgroup", kind: "Action")));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);

        var (workflow, context) = Workflow();
        Guid nameStage;
        await using (context)
        {
            var instance = (await workflow.FindAsync(item, Ct)).ShouldNotBeNull();
            instance.State.ShouldBe("Held");
            instance.ProvisioningAuthorized.ShouldBeTrue();
            nameStage = instance.Stages.Single(s => s is { Kind: "Action", State: "Active" } && s.Candidates.Contains(world.EntitlementOwner)).StageId;
        }

        await using var scope = Service(world.EntitlementOwner, route);
        (await scope.Service.GiveAccountNameAsync(item, nameStage, "adm.held", Ct)).AccountName.ShouldBe("adm.held");
    }

    /// <summary>Targets whose lookup fails: an error the runner itself can't handle.</summary>
    private sealed class BrokenTargets : IProvisioningTargets
    {
        public IProvisioningTarget? Find(string connectorKey) => throw new InvalidOperationException("The target registry is broken.");
    }
}
