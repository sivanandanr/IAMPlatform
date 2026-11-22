using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Workers.Provisioning;
using Shouldly;

namespace IAM.Persistence.Tests.Provisioning;

/// <summary>
/// Stage 9 slice 5 (DL-100) rules that need no database: a read the connector couldn't make is unknown, never "no match";
/// a target id has one spelling or is refused; and the undo counters move as the runner expects.
/// </summary>
public sealed class UndoTrustRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARealConnectorsFailedReadIsUnknownNotAMismatch()
    {
        var mock = new MockActiveDirectoryConnector(ConnectorSettings.Default, new InMemoryOperationLedger(), "DC=corp,DC=test");
        var users = mock.AddContainer("Users");
        var step = new TargetStep(Guid.CreateVersion7(), ActionCatalog.AccountCreate, ObjectTypes.Account, $"CN=ben,{users.Id}", ObjectTypes.Container, users.Id,
            new Dictionary<string, string?> { ["sAMAccountName"] = "ben" });
        var target = new ConnectorTarget(mock);
        (await target.VerifyAsync(step, CancellationToken.None)).ShouldBe(false, "a clean read: the account isn't there");

        mock.Faults.Unavailable = true;
        (await target.VerifyAsync(step, CancellationToken.None)).ShouldBeNull("a read that failed proves nothing");
    }

    [Theory]
    [InlineData("CN=\"Smith, Ben\",OU=Users,DC=corp,DC=test")]
    [InlineData("CN=Ben;OU=Users;DC=corp;DC=test")]
    [InlineData("2.5.4.3=Ben,OU=Users,DC=corp,DC=test")]
    [InlineData("3cn=Ben,OU=Users,DC=corp,DC=test")]
    [InlineData("CN=#04024869,OU=Users,DC=corp,DC=test")]
    [InlineData("CN=Ben+UID=ben,OU=Users,DC=corp,DC=test")]
    [InlineData("{8b1e43a5-2f6c-4d7e-9a10-0c3d5e7f9a1b}")]
    [InlineData("8b1e43a52f6c4d7e9a100c3d5e7f9a1b")]
    [InlineData("ben")]
    public void ATargetIdWithMoreThanOneSpellingIsRefused(string targetId) =>
        Should.Throw<AccountRejectedException>(() => AccountRules.ValidateTargetId(targetId));

    [Theory]
    [InlineData("CN=Ben Smith,OU=Users,DC=corp,DC=test")]
    [InlineData(@"CN=Smith\, Ben,OU=Users,DC=corp,DC=test")]
    [InlineData(@"CN=\#hash,OU=Users,DC=corp,DC=test")]
    [InlineData("8b1e43a5-2f6c-4d7e-9a10-0c3d5e7f9a1b")]
    public void APlainDistinguishedNameOrGuidIsAccepted(string targetId) =>
        Should.NotThrow(() => AccountRules.ValidateTargetId(targetId));

    [Fact]
    public void AnUndoKeepsItsFirstReasonAndCountsItsPasses()
    {
        var item = ProvisioningItem.Plan(Guid.CreateVersion7(), 1, new ItemDetails("ben", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null, null), "mock-ad", Now);
        item.Claim(Now, TimeSpan.FromMinutes(1));
        item.BeginUndoPass("VerifyMismatch");
        item.RetryUndoAt(Now.AddMinutes(1));
        item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Planned), i => i.Attempts.ShouldBe(0), i => i.UndoAttempts.ShouldBe(1));

        item.Claim(Now.AddMinutes(2), TimeSpan.FromMinutes(1));
        item.BeginUndoPass("Interrupted");
        item.ShouldSatisfyAllConditions(i => i.UndoReason.ShouldBe("VerifyMismatch"), i => i.UndoAttempts.ShouldBe(2));
    }

    [Fact]
    public void AStepsFingerprintNamesItsObjects()
    {
        var item = Guid.CreateVersion7();
        var binding = ConnectorBinding.Create(Guid.CreateVersion7(), "mock-ad", "OU=Users,DC=corp,DC=test", "OU=Groups,DC=corp,DC=test");
        var plan = ProvisioningPlanner.ForExistingAccount(item, 1, new ItemDetails("ben", binding.ApplicationId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null, null),
            "CN=ben,OU=Users,DC=corp,DC=test", "CN=Readers,OU=Groups,DC=corp,DC=test", binding, Now).Operations[0];
        var other = ProvisioningPlanner.ForExistingAccount(item, 1, new ItemDetails("ben", binding.ApplicationId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null, null),
            "CN=ceo,OU=Users,DC=corp,DC=test", "CN=Readers,OU=Groups,DC=corp,DC=test", binding, Now).Operations[0];
        ProvisioningPlanner.Fingerprint(plan).ShouldSatisfyAllConditions(f => f.Length.ShouldBe(64), f => f.ShouldNotBe(ProvisioningPlanner.Fingerprint(other)));
        ProvisioningPlanner.Fingerprint(plan).ShouldNotContain("ben");
    }
}
