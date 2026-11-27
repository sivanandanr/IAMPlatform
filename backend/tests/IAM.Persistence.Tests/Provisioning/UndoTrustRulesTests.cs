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
    [InlineData("commonName=Ben,OU=Users,DC=corp,DC=test")]
    [InlineData("CN=Ben,organizationalUnitName=Users,DC=corp,DC=test")]
    [InlineData("CN=Ben,OU=Users,domainComponent=corp,DC=test")]
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

    [Theory]
    [InlineData("CN=Ben  Smith,OU=Users,DC=corp,DC=test", "CN=Ben Smith,OU=Users,DC=corp,DC=test")]
    [InlineData("cn=Ben    Smith,ou=Users,dc=corp,dc=test", "CN=Ben Smith,OU=Users,DC=corp,DC=test")]
    [InlineData(@"CN=Ben\20\20Smith,OU=Users,DC=corp,DC=test", "CN=Ben Smith,OU=Users,DC=corp,DC=test")]
    [InlineData(@"CN=Ben \ Smith,OU=Users,DC=corp,DC=test", "CN=Ben Smith,OU=Users,DC=corp,DC=test")]
    [InlineData(@"CN=Ben\ \ Smith,OU=Users,DC=corp,DC=test", "CN=Ben Smith,OU=Users,DC=corp,DC=test")]
    public void RunsOfSpacesInAValueAreOneSpace(string spaced, string plain) =>
        AccountRules.TargetIdKey(spaced).ShouldBe(AccountRules.TargetIdKey(plain));

    [Fact]
    public void AGrantLockIsHeldOnlyByTheRunThatTookIt()
    {
        var (item, mine, theirs, now) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow);
        var held = GrantLock.Take(Guid.CreateVersion7(), Guid.CreateVersion7(), item, mine, now.AddMinutes(1));
        held.IsHeldBy(item, mine, now).ShouldBeTrue();
        held.IsHeldBy(item, theirs, now).ShouldBeFalse("a run that lost its lease is another run");
        held.IsHeldBy(item, mine, now.AddMinutes(2)).ShouldBeFalse("it ran out");
    }

    [Fact]
    public async Task AReadTheConnectorRefusesIsNoMatchNotUnknown()
    {
        // A step the connector refuses outright (an action it doesn't know) never reads differently later (DL-101).
        var mock = new MockActiveDirectoryConnector(ConnectorSettings.Default, new InMemoryOperationLedger(), "DC=corp,DC=test");
        var users = mock.AddContainer("Users");
        var step = new TargetStep(Guid.CreateVersion7(), "account.unheard.of", ObjectTypes.Account, $"CN=ben,{users.Id}", null, null, new Dictionary<string, string?>());
        (await new ConnectorTarget(mock).VerifyAsync(step, CancellationToken.None)).ShouldBe(false);
    }

    [Theory]
    [InlineData("ConnectorError", StepStatus.Unknown)]
    [InlineData("TimedOut", StepStatus.Unknown)]
    [InlineData("InvalidResponse", StepStatus.Unknown)]
    [InlineData("TargetUnavailable", StepStatus.Unknown)]
    [InlineData("SomeConnectorsOwnCode", StepStatus.Unknown)]
    [InlineData("Throttled", StepStatus.Unknown)]
    [InlineData("RateLimited", StepStatus.Retry)]
    public async Task AConnectorsOwnFaultOnAWriteHasAnUnknownOutcome(string code, StepStatus expected)
    {
        // DL-102, DL-103, DL-104: a fault may come after the write landed, and the connector's ledger keeps no failure. A
        // connector's own throttle may too; only the SDK's speed limit is known to come before any write.
        var step = new TargetStep(Guid.CreateVersion7(), ActionCatalog.AccountCreate, ObjectTypes.Account, "CN=ben,OU=Users,DC=corp,DC=test", null, null, new Dictionary<string, string?>());
        (await new ConnectorTarget(new FailingConnector(code)).RunAsync(step, CancellationToken.None)).Status.ShouldBe(expected);
    }

    /// <summary>A connector whose every write fails with one code.</summary>
    private sealed class FailingConnector(string code) : IConnector
    {
        public ConnectorCapabilities Capabilities => throw new NotSupportedException();

        public Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Of(operation, OperationStatus.Failed, code));

        public Task<VerificationResult> VerifyAsync(ConnectorOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ObjectPage> ListAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

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
