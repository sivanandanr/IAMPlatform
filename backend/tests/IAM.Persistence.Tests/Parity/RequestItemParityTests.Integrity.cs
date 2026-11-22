using IAM.BuildingBlocks.Messaging;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Workflow.Contracts;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 3 (DL-098), the stage 8 review's account and scope findings, on both engines: one directory object is one
/// account whatever its spelling; an account's name and target id never change; an entitlement's group must be in the
/// application's allowed container; and an existing account must be on the target before anything is granted to it.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task OneDirectoryObjectIsOneAccountWhateverItsSpelling()
    {
        var world = await WorldAsync();
        var dn = $"CN=Case {Guid.NewGuid():N},OU=Users,DC=corp,DC=test";
        await LinkAsync(world.Application, $"c{Guid.NewGuid():N}"[..12], dn);
        await Should.ThrowAsync<Exception>(() => LinkAsync(world.Application, $"c{Guid.NewGuid():N}"[..12], dn.ToLowerInvariant()));

        // A recorded account's name and target id never change, whoever writes.
        var table = IsSqlServer ? "[account].[Account]" : "account.account";
        foreach (var login in new[] { db.AccountAppConnectionString, db.MigratorConnectionString })
        {
            foreach (var set in new[] { $"{Col("Name")} = 'mallory', {Col("NameKey")} = 'mallory'", $"{Col("TargetId")} = 'CN=Domain Admins,CN=Users,DC=corp,DC=test', {Col("TargetIdKey")} = 'cn=domain admins,cn=users,dc=corp,dc=test'" })
            {
                var sql = $"UPDATE {table} SET {set} WHERE {Col("Id")} = '{world.Primary}'";
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldNotBeNullOrEmpty();
            }
        }
    }

    [Fact]
    public async Task AGroupOutsideTheApplicationsContainerIsNeverGranted()
    {
        var world = await WorldAsync();
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));

        // The application's groups are allowed only under another container: the entitlement's group is out of scope.
        var elsewhere = ConnectorBinding.Create(world.Application, "mock-ad", AccountContainer, "OU=App Groups,DC=corp,DC=test");
        (await HandleAsync(world, await WorkflowEnvelopeAsync(item, ApprovalAuthorized.MessageType), elsewhere)).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.Problem.ShouldBe("EntitlementOutOfScope");
    }

    [Fact]
    public async Task AnExistingAccountMustBeOnTheTargetBeforeAnythingIsGranted()
    {
        var item = await PlannedExistingAsync();
        var target = new ScriptedTarget(StepStatus.Done) { Exists = false };
        (await RunAsync(item, target)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("AccountNotOnTarget"));
        target.Runs.ShouldBe(0, "nothing was granted");
    }
}
