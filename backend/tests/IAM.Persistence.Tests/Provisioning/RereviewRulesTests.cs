using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Workflow.Domain.Approvals;
using Shouldly;

namespace IAM.Persistence.Tests.Provisioning;

/// <summary>
/// Stage 9 slice 4 (DL-099) rules that need no database: what an item's undo is, the facts the runner checks, the recorded
/// account name's shape and hash, and the one form a target id is unique in.
/// </summary>
public sealed class RereviewRulesTests
{
    private const string Container = "OU=Users,OU=App,DC=example,DC=test";
    private const string Group = "CN=App-Readers,OU=Groups,DC=example,DC=test";
    private const string Groups = "OU=Groups,DC=example,DC=test";
    private static readonly Guid App = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly ConnectorBinding Binding = ConnectorBinding.Create(App, "mock-ad", Container, Groups);

    [Fact]
    public void ANewAccountIsUndoneOnlyIfThisItemCreatedIt()
    {
        var item = Guid.CreateVersion7();
        var plan = NewAccountPlan(item);
        ProvisioningPlanner.Undo(item, plan).ShouldBeEmpty("nothing ran");

        Done(plan[0], "Applied");
        var undo = ProvisioningPlanner.Undo(item, plan);
        undo.Select(o => (o.Step, o.Action, o.TargetId, o.RelatedId)).ShouldBe(
        [
            (11, ProvisioningPlanner.EntitlementRemove, $"CN=adm.ben,{Container}", (string?)Group),
            (12, ProvisioningPlanner.AccountDisable, $"CN=adm.ben,{Container}", (string?)null),
        ]);
        undo.Select(o => o.Id).ShouldBe([ProvisioningOperation.OperationId(item, 11), ProvisioningOperation.OperationId(item, 12)], "fixed ids, so a resumed undo is the same");

        // A create that met someone else's object never succeeded: that object is never touched.
        var taken = NewAccountPlan(item);
        taken[0].Start();
        taken[0].Fail("TargetExists", Now);
        ProvisioningPlanner.Undo(item, taken).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Applied", true)]
    [InlineData("AlreadyInState", false)]
    [InlineData("Done", false)]
    public void AnExistingAccountLosesOnlyAnEntitlementThisItemAdded(string code, bool removed)
    {
        var item = Guid.CreateVersion7();
        var plan = ProvisioningPlanner.ForExistingAccount(item, 1, Details(Guid.CreateVersion7(), null, null), "CN=ben,OU=Users,DC=example,DC=test", Group, Binding, Now).Operations.ToList();
        Done(plan[0], code);
        var undo = ProvisioningPlanner.Undo(item, plan);
        if (removed)
        {
            undo.Single().ShouldSatisfyAllConditions(o => o.Step.ShouldBe(11), o => o.Action.ShouldBe(ProvisioningPlanner.EntitlementRemove), o => o.RelatedId.ShouldBe(Group));
        }
        else
        {
            undo.ShouldBeEmpty("access the person already had is never taken away");
        }
    }

    [Fact]
    public void TheRunnerActsOnlyWhenItsRowSaysWhatTheDecisionSays()
    {
        var details = Details(null, "Secondary", "adm.ben");
        var stored = ProvisioningItem.Plan(Guid.CreateVersion7(), 2, details, "mock-ad", Now);
        ProvisioningRunner.SameFacts(stored, ProvisioningItem.Plan(stored.Id, 2, details, "mock-ad", Now)).ShouldBeTrue();
        foreach (var other in new[]
        {
            details with { Beneficiary = "mallory" },
            details with { AccountName = "adm.other" },
            details with { NewAccountType = "Service" },
            details with { EntitlementVersionId = Guid.CreateVersion7() },
            details with { EntitlementId = Guid.CreateVersion7() },
            details with { ApplicationId = Guid.CreateVersion7() },
        })
        {
            ProvisioningRunner.SameFacts(stored, ProvisioningItem.Plan(stored.Id, 2, other, "mock-ad", Now)).ShouldBeFalse(other.ToString());
        }

        ProvisioningRunner.SameFacts(stored, ProvisioningItem.Plan(stored.Id, 2, details, "other-connector", Now)).ShouldBeFalse();
    }

    [Fact]
    public void AnItemPostponedForAChangingApprovalUsesNoAttempt()
    {
        var item = ProvisioningItem.Plan(Guid.CreateVersion7(), 1, Details(Guid.CreateVersion7(), null, null), "mock-ad", Now);
        item.Claim(Now, TimeSpan.FromMinutes(1));
        item.Postpone(Now.AddMinutes(1));
        item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Planned), i => i.Attempts.ShouldBe(0), i => i.NextAttemptAtUtc.ShouldBe(Now.AddMinutes(1)));
    }

    [Fact]
    public void TheRecordedAccountNameIsAHashAndAnAssignmentOrNothing()
    {
        AccountNameHash.Of("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        var hash = AccountNameHash.Of("adm.ben");
        Completion(null, null).NameIsWellFormed.ShouldBeTrue("an older log, or no new account");
        Completion(null, hash).NameIsWellFormed.ShouldBeTrue("the naming rule's proposal");
        Completion(Guid.CreateVersion7(), hash).NameIsWellFormed.ShouldBeTrue();
        Completion(Guid.CreateVersion7(), null).NameIsWellFormed.ShouldBeFalse("an assignment without its hash");
        Completion(Guid.Empty, hash).NameIsWellFormed.ShouldBeFalse();
        Completion(null, hash.ToUpperInvariant()).NameIsWellFormed.ShouldBeFalse();
        Completion(null, hash[..63]).NameIsWellFormed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("CN=Ben Smith,OU=Users,DC=corp,DC=test", "cn=ben smith,ou=users,dc=corp,dc=test")]
    [InlineData("CN=Ben Smith, OU=Users , DC=corp,DC=test", "cn=ben smith,ou=users,dc=corp,dc=test")]
    [InlineData(" cn = Ben Smith ,ou=USERS,dc=CORP,dc=test", "cn=ben smith,ou=users,dc=corp,dc=test")]
    [InlineData(@"CN=Ben\20Smith,OU=Users,DC=corp,DC=test", "cn=ben smith,ou=users,dc=corp,dc=test")]
    [InlineData(@"CN=Smith\, Ben,OU=Users,DC=corp,DC=test", @"cn=smith\, ben,ou=users,dc=corp,dc=test")]
    [InlineData(@"CN=Smith\2C Ben,OU=Users,DC=corp,DC=test", @"cn=smith\, ben,ou=users,dc=corp,dc=test")]
    [InlineData(@"CN=\ Ben\ ,OU=Users,DC=corp,DC=test", @"cn=\ ben\ ,ou=users,dc=corp,dc=test")]
    [InlineData("8B1E43A5-2F6C-4D7E-9A10-0C3D5E7F9A1B", "8b1e43a5-2f6c-4d7e-9a10-0c3d5e7f9a1b")]
    [InlineData("CN=A+UID=b,DC=test", "cn=a+uid=b,dc=test")]
    public void OneDirectoryObjectHasOneKeyWhateverItsSpelling(string targetId, string key) =>
        AccountRules.TargetIdKey(targetId).ShouldBe(key);

    [Theory]
    [InlineData("CN=Bén,OU=Users,DC=corp,DC=test")]
    [InlineData("CN=KKelvin,DC=test")]
    [InlineData("CN=Ben\tSmith,DC=test")]
    public void ATargetIdIsPrintableAscii(string targetId) =>
        Should.Throw<AccountRejectedException>(() => AccountRules.ValidateTargetId(targetId));

    private static List<ProvisioningOperation> NewAccountPlan(Guid item) =>
        ProvisioningPlanner.ForNewAccount(item, 2, Details(null, "Secondary", "adm.ben"), Group, Binding, Now).Operations.ToList();

    private static void Done(ProvisioningOperation operation, string code)
    {
        operation.Start();
        operation.Complete(code, Now);
    }

    private static ActionCompletion Completion(Guid? assignment, string? hash) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), PersonId.Create("emp:1001"), null, null, 1, assignment, hash);

    private static ItemDetails Details(Guid? account, string? newType, string? name) =>
        new("ben", App, Guid.CreateVersion7(), Guid.CreateVersion7(), account, newType, name);
}
