using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Workers.Provisioning;
using Microsoft.Extensions.DependencyInjection;
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
    [InlineData("RateLimited", StepStatus.Unknown)]
    public async Task AConnectorsOwnFaultOnAWriteHasAnUnknownOutcome(string code, StepStatus expected)
    {
        // DL-102, DL-103, DL-104: a fault may come after the write landed, and the connector's ledger keeps no failure. A
        // connector's own throttle may too; only the SDK's speed limit is known to come before any write. A connector not built
        // on the SDK can't say so either, whatever code it reports (DL-105).
        var step = new TargetStep(Guid.CreateVersion7(), ActionCatalog.AccountCreate, ObjectTypes.Account, "CN=ben,OU=Users,DC=corp,DC=test", null, null, new Dictionary<string, string?>());
        (await new ConnectorTarget(new FailingConnector(code)).RunAsync(step, CancellationToken.None)).Status.ShouldBe(expected);
    }

    [Fact]
    public async Task OnlyTheSdksOwnSpeedLimitIsAPlainRetry()
    {
        // DL-105: a connector on the SDK, with one operation a second and a clock that doesn't move: the second write is refused
        // by the SDK before the connector runs, so nothing was written and it is retried plainly.
        var mock = new MockActiveDirectoryConnector(ConnectorSettings.Default with { MaxOperationsPerSecond = 1 }, new InMemoryOperationLedger(), "DC=corp,DC=test", new StoppedClock());
        var users = mock.AddContainer("Users");
        TargetStep Disable() => new(Guid.CreateVersion7(), ActionCatalog.AccountDisable, ObjectTypes.Account, $"CN=ben,{users.Id}", null, null, new Dictionary<string, string?>());
        var target = new ConnectorTarget(mock);
        await target.RunAsync(Disable(), CancellationToken.None);
        (await target.RunAsync(Disable(), CancellationToken.None)).ShouldBe(new StepResult(StepStatus.Retry, ResultCodes.RateLimited));
    }

    /// <summary>A clock whose timer never moves: a speed limit's capacity never comes back.</summary>
    private sealed class StoppedClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    /// <summary>A connector whose every write fails with one code.</summary>
    /// <summary>A durable ledger store holding one stored status for every operation.</summary>
    private sealed class StoredLedger(string status) : IProvisioningLedger
    {
        public Task<OperationLedgerRecord?> FindAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult<OperationLedgerRecord?>(OperationLedgerRecord.Create(operationId, new string('c', 64), status, ResultCodes.RateLimited, DateTimeOffset.UtcNow));

        public Task RecordAsync(OperationLedgerRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A connector not on the SDK whose write throws an InvalidDataException, as a garbled answer would.</summary>
    private sealed class GarblingConnector : IConnector
    {
        public ConnectorCapabilities Capabilities => throw new NotSupportedException();

        public Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken) =>
            throw new InvalidDataException("The answer couldn't be decoded.");

        public Task<VerificationResult> VerifyAsync(ConnectorOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ObjectPage> ListAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailingConnector(string code) : IConnector
    {
        public ConnectorCapabilities Capabilities => throw new NotSupportedException();

        public Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Of(operation, OperationStatus.Failed, code));

        public Task<VerificationResult> VerifyAsync(ConnectorOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(new VerificationResult(false, code));

        public Task<ObjectPage> ListAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AConnectorOnTheSdkThatImplementsExecuteAgainIsNotTrusted()
    {
        // DL-106: built on ConnectorBase, but its own ExecuteAsync skips the SDK's pipeline; its RateLimited may follow a write.
        var step = new TargetStep(Guid.CreateVersion7(), ActionCatalog.AccountCreate, ObjectTypes.Account, "CN=ben,OU=Users,DC=corp,DC=test", null, null, new Dictionary<string, string?>());
        ConnectorTarget.RunsSdkPipeline(typeof(ReimplementingConnector)).ShouldBeFalse();
        (await new ConnectorTarget(new ReimplementingConnector()).RunAsync(step, CancellationToken.None)).Status.ShouldBe(StepStatus.Unknown);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("applied")]
    [InlineData("Bogus")]
    public async Task TheDurableLedgerRefusesARecordTheSdkNeverWritesAndTheTargetNamesIt(string stored)
    {
        // DL-106, DL-107: a stored failure (or a status the SDK doesn't know) is refused on read as the SDK's own damage, and the
        // connector target turns that, and only that, into LedgerDamaged. The database check makes such a row impossible, so
        // the store is a stub here; the whole chain against a real database is in the parity tests.
        var services = new ServiceCollection().AddSingleton<IProvisioningLedger>(new StoredLedger(stored)).BuildServiceProvider();
        await using var _ = services;
        var ledger = new DurableOperationLedger(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        await Should.ThrowAsync<LedgerRecordDamagedException>(() => ledger.FindAsync(Guid.CreateVersion7(), CancellationToken.None));

        var mock = new MockActiveDirectoryConnector(ConnectorSettings.Default, ledger, "DC=corp,DC=test");
        var users = mock.AddContainer("Users");
        var step = new TargetStep(Guid.CreateVersion7(), ActionCatalog.AccountCreate, ObjectTypes.Account, $"CN=ben,{users.Id}", ObjectTypes.Container, users.Id,
            new Dictionary<string, string?> { ["sAMAccountName"] = "ben" });
        (await new ConnectorTarget(mock).RunAsync(step, CancellationToken.None)).ShouldBe(new StepResult(StepStatus.Unknown, StepCodes.LedgerDamaged));
        (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN=ben,{users.Id}"), CancellationToken.None)).ShouldBeNull("nothing was written");
    }

    [Fact]
    public async Task AnyOtherFaultOfTheConnectorIsNotTakenForDamage()
    {
        // DL-107: an InvalidDataException (a garbled answer, say) is a call that broke off, never a damaged ledger record.
        var step = new TargetStep(Guid.CreateVersion7(), ActionCatalog.AccountCreate, ObjectTypes.Account, "CN=ben,OU=Users,DC=corp,DC=test", null, null, new Dictionary<string, string?>());
        await Should.ThrowAsync<InvalidDataException>(() => new ConnectorTarget(new GarblingConnector()).RunAsync(step, CancellationToken.None));
    }

    [Fact]
    public void EveryConnectorShippedRunsTheSdkPipeline()
    {
        // DL-106: no production connector implements IConnector.ExecuteAsync again, so its RateLimited is the SDK's own word.
        // Every IAM assembly beside the tests (DL-107), so a connector added later is checked too; the three known ones must be
        // among them.
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "IAM.*.dll")
            .Where(f => !Path.GetFileName(f).Contains(".Tests", StringComparison.Ordinal))
            .Select(f => System.Reflection.Assembly.LoadFrom(f))
            .ToList();
        assemblies.ShouldContain(typeof(ConnectorBase).Assembly);
        assemblies.ShouldContain(typeof(MockActiveDirectoryConnector).Assembly);
        assemblies.ShouldContain(typeof(ConnectorTarget).Assembly);
        var shipped = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && typeof(IConnector).IsAssignableFrom(t))
            .ToList();
        shipped.ShouldNotBeEmpty();
        shipped.ShouldAllBe(t => ConnectorTarget.RunsSdkPipeline(t));
    }

    [Fact]
    public async Task AReadAConnectorNotOnTheSdkSaysWasRateLimitedIsTriedAgain()
    {
        // DL-106: busy, not a mismatch; the SDK itself never answers a read with it.
        var step = new TargetStep(Guid.CreateVersion7(), ActionCatalog.AccountCreate, ObjectTypes.Account, "CN=ben,OU=Users,DC=corp,DC=test", null, null, new Dictionary<string, string?>());
        (await new ConnectorTarget(new FailingConnector(ResultCodes.RateLimited)).VerifyAsync(step, CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>On the SDK, but its writes skip the pipeline: each one "lands" and reports RateLimited.</summary>
    private sealed class ReimplementingConnector() : ConnectorBase(ConnectorSettings.Default, new InMemoryOperationLedger()), IConnector
    {
        public override ConnectorCapabilities Capabilities { get; } = new(
            "test.reimplementing", false, [ActionCatalog.AccountCreate], [new ObjectTypeCapability(ObjectTypes.Account, false, ["displayName"])], 10, 1);

        public override Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectorHealth(HealthStatus.Healthy, null, false));

        Task<OperationResult> IConnector.ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Of(operation, OperationStatus.Failed, ResultCodes.RateLimited));

        protected override Task<ObjectSnapshot?> ReadCoreAsync(ObjectRef target, CancellationToken cancellationToken) => Task.FromResult<ObjectSnapshot?>(null);

        protected override Task<OperationResult> ApplyAsync(ConnectorOperation operation, ObjectSnapshot? current, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Of(operation, OperationStatus.Applied));

        protected override Task<ObjectPage> ListCoreAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectPage([], null));
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
