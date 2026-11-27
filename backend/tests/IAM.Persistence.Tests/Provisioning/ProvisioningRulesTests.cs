using System.Globalization;
using IAM.BuildingBlocks.Messaging;
using IAM.Connectors.Sdk;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using IAM.Workers.Provisioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace IAM.Persistence.Tests.Provisioning;

/// <summary>Stage 8 slice 5a (DL-093) rules that need no database: planning, the bindings, the ledger record and the R-037 comparison.</summary>
public sealed class ProvisioningRulesTests
{
    private const string Container = "OU=Users,OU=App,DC=example,DC=test";
    private const string Group = "CN=App-Readers,OU=Groups,DC=example,DC=test";
    private const string Groups = "OU=Groups,DC=example,DC=test";
    private static readonly Guid App = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnOperationIdIsFixedByItsItemAndStep()
    {
        var item = Guid.CreateVersion7();
        ProvisioningOperation.OperationId(item, 1).ShouldBe(ProvisioningOperation.OperationId(item, 1));
        ProvisioningOperation.OperationId(item, 1).ShouldNotBe(ProvisioningOperation.OperationId(item, 2));
        ProvisioningOperation.OperationId(item, 1).ShouldNotBe(ProvisioningOperation.OperationId(Guid.CreateVersion7(), 1));
        ProvisioningOperation.OperationId(item, 1).Version.ShouldBe(8, "a name-based UUID (RFC 9562)");
    }

    [Fact]
    public void ThePlannerUsesTheConnectorCatalogsCodes()
    {
        ProvisioningPlanner.AccountCreate.ShouldBe(ActionCatalog.AccountCreate);
        ProvisioningPlanner.AccountEnable.ShouldBe(ActionCatalog.AccountEnable);
        ProvisioningPlanner.EntitlementAdd.ShouldBe(ActionCatalog.EntitlementAdd);
        ProvisioningPlanner.AccountObject.ShouldBe(ObjectTypes.Account);
        ProvisioningPlanner.EntitlementObject.ShouldBe(ObjectTypes.Entitlement);
        ProvisioningPlanner.ContainerObject.ShouldBe(ObjectTypes.Container);
    }

    [Fact]
    public void ANewAccountIsCreatedGivenTheEntitlementThenEnabled()
    {
        var item = Guid.CreateVersion7();
        var binding = ConnectorBinding.Create(App, "mock-ad", Container, Groups);
        var plan = ProvisioningPlanner.ForNewAccount(item, 3, Details(null, "Secondary", "adm.ben"), Group, binding, Now);

        plan.Item.State.ShouldBe(ProvisioningState.Planned);
        plan.Item.ConnectorKey.ShouldBe("mock-ad");
        plan.Item.ApprovalActions.ShouldBe(3);
        plan.Operations.Select(o => (o.Step, o.Action, o.TargetId, o.RelatedType, o.RelatedId)).ShouldBe(
        [
            (1, "account.create", $"CN=adm.ben,{Container}", "container", Container),
            (2, "account.entitlement.add", $"CN=adm.ben,{Container}", "entitlement", Group),
            (3, "account.enable", $"CN=adm.ben,{Container}", (string?)null, (string?)null),
        ]);
        plan.Operations[0].Attributes.ShouldBe("{\"sAMAccountName\":\"adm.ben\"}");
        plan.Operations.Select(o => o.Id).ShouldBe([.. Enumerable.Range(1, 3).Select(s => ProvisioningOperation.OperationId(item, s))]);
        plan.Operations.ShouldAllBe(o => o.State == ProvisioningState.Planned && o.ItemId == item);

        // A name that couldn't sit in a DN, no name, an existing account or another application's binding: no plan.
        foreach (var bad in new[] { Details(null, "Secondary", "adm,ben"), Details(null, "Secondary", null), Details(Guid.CreateVersion7(), null, null) })
        {
            Should.Throw<ArgumentException>(() => ProvisioningPlanner.ForNewAccount(item, 3, bad, Group, binding, Now));
        }

        Should.Throw<ArgumentException>(() => ProvisioningPlanner.ForNewAccount(item, 3, Details(null, "Secondary", "adm.ben"), Group, ConnectorBinding.Create(Guid.CreateVersion7(), "mock-ad", Container, Groups), Now));
    }

    [Fact]
    public void AnExistingAccountIsOnlyGivenTheEntitlement()
    {
        var item = Guid.CreateVersion7();
        var plan = ProvisioningPlanner.ForExistingAccount(item, 2, Details(Guid.CreateVersion7(), null, null), $"CN=ben,{Container}", Group, ConnectorBinding.Create(App, "mock-ad", Container, Groups), Now);
        plan.Operations.Select(o => (o.Step, o.Action, o.TargetId, o.RelatedId)).ShouldBe([(1, "account.entitlement.add", $"CN=ben,{Container}", Group)]);
        Should.Throw<ArgumentException>(() => ProvisioningPlanner.ForExistingAccount(item, 2, Details(null, "Secondary", "x"), "CN=x", Group, ConnectorBinding.Create(App, "mock-ad", Container, Groups), Now));
    }

    [Theory]
    [InlineData("other-connector", Container)]
    [InlineData("mock-ad", "DC=example,DC=test")]
    [InlineData("mock-ad", "OU=Users")]
    [InlineData("mock-ad", "OU=Users,DC=example,DC=test;DROP")]
    [InlineData("mock-ad", "OU=Users\n,DC=example,DC=test")]
    [InlineData("mock-ad", "CN=Users,DC=example,DC=test")]
    [InlineData("mock-ad", " OU=Users,DC=example,DC=test")]
    [InlineData(null, Container)]
    [InlineData("mock-ad", null)]
    public void ABindingNamesAKnownConnectorAndAnOuPath(string? connector, string? container) =>
        Should.Throw<ArgumentException>(() => ConnectorBinding.Create(App, connector, container, Groups));

    [Fact]
    public void AnEntitlementsGroupMustBeAPlainGroupInTheAllowedContainer()
    {
        var binding = ConnectorBinding.Create(App, "mock-ad", Container, Groups);
        ProvisioningPlanner.IsInGroupContainer(Group, binding).ShouldBeTrue();
        ProvisioningPlanner.IsInGroupContainer("cn=App-Readers,ou=groups,dc=example,dc=test", binding).ShouldBeTrue("DNs compare ignoring case");
        foreach (var outside in new[]
        {
            "CN=Domain Admins,CN=Users,DC=example,DC=test",
            "CN=Admins,OU=Other,OU=Groups,DC=example,DC=test",
            "CN=A,OU=Sub,OU=Groups,DC=example,DC=test",
            "CN=A+CN=B,OU=Groups,DC=example,DC=test",
            "OU=Groups,DC=example,DC=test",
            "CN=,OU=Groups,DC=example,DC=test",
        })
        {
            ProvisioningPlanner.IsInGroupContainer(outside, binding).ShouldBeFalse(outside);
        }

        Should.Throw<ArgumentException>(() => ConnectorBinding.Create(App, "mock-ad", Container, "DC=example,DC=test"));
        Should.Throw<ArgumentException>(() => ConnectorBinding.Create(App, "mock-ad", Container, null));
    }

    [Fact]
    public void ABindingNeedsItsApplication() =>
        Should.Throw<ArgumentException>(() => ConnectorBinding.Create(Guid.Empty, "mock-ad", Container, Groups));

    [Fact]
    public void OnlyAnIntegrityBlockHasNoTrustedActionCount()
    {
        var item = Guid.CreateVersion7();
        ProvisioningItem.Block(item, null, BlockReason.Integrity, "AuditMissing", null, Now).ApprovalActions.ShouldBeNull();
        ProvisioningItem.Block(item, 4, BlockReason.ManualWork, null, Details(null, "Secondary", "a"), Now).Problem.ShouldBe("ManualWork");
        Should.Throw<ArgumentException>(() => ProvisioningItem.Block(item, 4, BlockReason.Integrity, "AuditMissing", null, Now));
        Should.Throw<ArgumentException>(() => ProvisioningItem.Block(item, null, BlockReason.Integrity, null, null, Now));
        Should.Throw<ArgumentException>(() => ProvisioningItem.Block(item, null, BlockReason.ManualWork, null, null, Now));
        Should.Throw<ArgumentException>(() => ProvisioningItem.Block(item, 4, BlockReason.ManualWork, "AuditMissing", null, Now));
        Should.Throw<ArgumentException>(() => ProvisioningItem.Block(Guid.Empty, 4, BlockReason.ManualWork, null, null, Now));
        Should.Throw<ArgumentException>(() => ProvisioningItem.Block(item, 0, BlockReason.ManualWork, null, null, Now));
        Should.Throw<ArgumentException>(() => ProvisioningItem.Block(item, 4, (BlockReason)99, null, null, Now));
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "Applied", null)]
    [InlineData("abc", "Applied", null)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "", null)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "Applied!", null)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "Applied", "")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "Applied", "bad code")]
    public void ALedgerRecordHoldsOnlyAFingerprintAStatusAndACode(string fingerprint, string status, string? code) =>
        Should.Throw<ArgumentException>(() => OperationLedgerRecord.Create(Guid.CreateVersion7(), fingerprint, status, code, Now));

    [Fact]
    public void TheLogMatchesTheAuditOnlyActionForAction()
    {
        var id = Guid.CreateVersion7();
        var actions = new[] { Action(id, 1, "Bound", 1), Action(id, 2, "Decided", 3) };
        var good = actions.Select(a => Audit(id, a)).ToList();
        ApprovalIntegrityCheck.Compare(id, actions, good).ShouldBeNull();

        // Refusals and damage reports about the instance are not actions and don't count.
        ApprovalIntegrityCheck.Compare(id, actions, [.. good, Entry("workflow.approval.refused", "Denied", new() { ["step"] = "decide" })]).ShouldBeNull();

        ApprovalIntegrityCheck.Compare(id, actions, good.Take(1).ToList()).ShouldBe("AuditMissing");
        ApprovalIntegrityCheck.Compare(id, actions, [.. good, good[1]]).ShouldBe("AuditDuplicate");
        ApprovalIntegrityCheck.Compare(id, actions, [good[0], good[1] with { Intact = false }]).ShouldBe("AuditChanged");
        ApprovalIntegrityCheck.Compare(id, actions, [good[0], good[1] with { Outcome = "Failed" }]).ShouldBe("AuditUnexpected");
        ApprovalIntegrityCheck.Compare(id, actions, [.. good, Audit(id, Action(id, 3, "Decided", 4))]).ShouldBe("AuditUnexpected", "an audited action the log doesn't have");
        ApprovalIntegrityCheck.Compare(id, actions, [good[0], good[1] with { Payload = With(good[1], "sequence", "x") }]).ShouldBe("AuditUnexpected");
        ApprovalIntegrityCheck.Compare(id, actions, [good[0], good[1] with { Payload = With(good[1], "actionHash", new string('0', 64)) }]).ShouldBe("HashMismatch");
        ApprovalIntegrityCheck.Compare(id, actions, [good[0], good[1] with { Payload = With(good[1], "versionAfter", "9") }]).ShouldBe("HashMismatch");
        ApprovalIntegrityCheck.Compare(id, actions, [good[0], good[1] with { Action = "workflow.approval.completed" }]).ShouldBe("HashMismatch", "another kind of action");

        // A log changed after it was audited no longer matches its hash.
        var forged = new[] { actions[0], Action(id, 2, "Decided", 3, "{\"forged\":true}") };
        ApprovalIntegrityCheck.Compare(id, forged, good).ShouldBe("HashMismatch");
    }

    [Fact]
    public void TheWorkerStartsOnlyWithEverySettingAndAValidAllowlist()
    {
        using (var host = ProvisioningWorkerHost.Build([], Complete))
        {
            host.Services.GetServices<IHostedService>().Count().ShouldBe(2, "the consumer and the runner");
            host.Services.GetRequiredService<IOperationLedger>().ShouldBeOfType<DurableOperationLedger>();
        }

        // The undo and postponement limits come from configuration too (DL-101).
        using (var host = ProvisioningWorkerHost.Build([], [.. Complete, new("Provisioning:Runner:UndoMaxAttempts", "3"), new("Provisioning:Runner:MaxPostponements", "7")]))
        {
            var settings = host.Services.GetRequiredService<ProvisioningRunnerSettings>();
            settings.ShouldSatisfyAllConditions(s => s.UndoMaxAttempts.ShouldBe(3), s => s.MaxPostponements.ShouldBe(7));
        }

        foreach (var schema in new[] { "provisioning", "workflow", "audit", "request", "catalog", "account" })
        {
            Should.Throw<InvalidOperationException>(() => ProvisioningWorkerHost.Build([], Complete.Where(s => s.Key != $"ConnectionStrings:{schema}")), schema)
                .Message.ShouldContain(schema);
        }

        Should.Throw<InvalidOperationException>(() => ProvisioningWorkerHost.Build([], Complete.Where(s => s.Key != "Messaging:RabbitMq:UserName")));
        Should.Throw<InvalidOperationException>(() => ProvisioningWorkerHost.Build([], [.. Complete, new("Provisioning:Connectors:0:ApplicationId", "not-a-guid")]));
        Should.Throw<InvalidOperationException>(() => ProvisioningWorkerHost.Build([], [.. Complete, .. Binding(0, App, "unknown")]));
        Should.Throw<InvalidOperationException>(() => ProvisioningWorkerHost.Build([], [.. Complete, .. Binding(0, App, "mock-ad"), .. Binding(1, App, "mock-ad")]), "an application twice");

        var bindings = ProvisioningWorkerHost.Bindings(new ConfigurationBuilder().AddInMemoryCollection([.. Binding(0, App, "mock-ad")]).Build());
        bindings.ShouldHaveSingleItem().ShouldBe(new ConnectorBinding(App, "mock-ad", Container, Groups));
        ProvisioningWorkerHost.Bindings(new ConfigurationBuilder().Build()).ShouldBeEmpty("no allowlist: everything is manual work");
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("00000000000000000000000000000000", false)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData(null, false)]
    public void OnlyAW3CTraceIdIsCarriedAsTheCorrelationId(string? value, bool carried)
    {
        MessageCorrelation.IsTraceId(value).ShouldBe(carried);
        using var trace = MessageCorrelation.StartFor(value, "test");
        (trace is not null).ShouldBe(carried);
        if (carried)
        {
            MessageCorrelation.Current().ShouldBe(value);
        }
    }

    [Theory]
    [InlineData(OperationStatus.Applied, StepStatus.Done)]
    [InlineData(OperationStatus.AlreadyInState, StepStatus.Done)]
    [InlineData(OperationStatus.Failed, StepStatus.Unknown)] // DL-103: a failed write may have landed; only the SDK's speed limit (DL-104) is a plain retry
    [InlineData(OperationStatus.Rejected, StepStatus.Rejected)]
    [InlineData(OperationStatus.Unsupported, StepStatus.Rejected)]
    public async Task AConnectorsOutcomeBecomesAStepStatus(OperationStatus outcome, StepStatus expected)
    {
        var step = new TargetStep(Guid.CreateVersion7(), "account.enable", "account", "CN=a,OU=Users,DC=example,DC=test", null, null, new Dictionary<string, string?>());
        var result = await new ConnectorTarget(new FixedConnector(outcome)).RunAsync(step, CancellationToken.None);
        result.Status.ShouldBe(expected);
        ConnectorTarget.ToOperation(step).ShouldSatisfyAllConditions(o => o.OperationId.ShouldBe(step.OperationId), o => o.Related.ShouldBeNull(), o => o.Secret.ShouldBeNull());
    }

    [Fact]
    public void RunnerSettingsAreBoundedAndTheDelayGrows()
    {
        var settings = new ProvisioningRunnerSettings(retryDelaySeconds: 30);
        settings.DelayAfter(1).ShouldBe(TimeSpan.FromSeconds(30));
        settings.DelayAfter(3).ShouldBe(TimeSpan.FromSeconds(120));
        settings.DelayAfter(40).ShouldBe(TimeSpan.FromHours(1), "never more than an hour");
        Should.Throw<ArgumentOutOfRangeException>(() => new ProvisioningRunnerSettings(batchSize: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new ProvisioningRunnerSettings(leaseSeconds: 10));
        Should.Throw<ArgumentOutOfRangeException>(() => new ProvisioningRunnerSettings(maxAttempts: 51));
    }

    [Fact]
    public void AnItemMovesOnlyForwardAndSucceedsWithItsRecordedAccount()
    {
        var item = ProvisioningPlanner.ForNewAccount(Guid.CreateVersion7(), 3, Details(null, "Secondary", "adm.ben"), Group, ConnectorBinding.Create(App, "mock-ad", Container, Groups), Now).Item;
        Should.Throw<InvalidOperationException>(() => item.Fail("X", Now), "only a running item moves on");
        item.CanClaim(Now).ShouldBeTrue();
        item.Claim(Now, TimeSpan.FromMinutes(5));
        item.CanClaim(Now.AddMinutes(1)).ShouldBeFalse("its lease holds");
        item.CanClaim(Now.AddMinutes(6)).ShouldBeTrue("a lease that ran out can be taken over");
        Should.Throw<InvalidOperationException>(() => item.Succeed(null, Now), "a new account is recorded first");
        item.RetryAt(Now.AddMinutes(1));
        item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Planned), i => i.Attempts.ShouldBe(1), i => i.LeaseUntilUtc.ShouldBeNull());
        item.CanClaim(Now).ShouldBeFalse("not before its next attempt");
        item.Claim(Now.AddMinutes(2), TimeSpan.FromMinutes(5));
        item.Succeed(Guid.CreateVersion7(), Now);
        item.State.ShouldBe(ProvisioningState.Succeeded);
        item.CanClaim(Now.AddDays(1)).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => item.Correlate("0123456789abcdef0123456789abcdeg"));
        item.Correlate("0123456789abcdef0123456789abcdef");
        Should.Throw<InvalidOperationException>(() => item.Correlate("0123456789abcdef0123456789abcdef"), "once");
        ProvisioningItem.ProblemCode("TargetUnavailable", "x").ShouldBe("TargetUnavailable");
        ProvisioningItem.ProblemCode("has spaces", "Rejected").ShouldBe("Rejected");
    }

    private static readonly KeyValuePair<string, string?>[] Complete =
    [
        new("IAM_DB_PROVIDER", "PostgreSql"),
        new("ConnectionStrings:provisioning", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("ConnectionStrings:workflow", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("ConnectionStrings:audit", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("ConnectionStrings:request", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("ConnectionStrings:catalog", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("ConnectionStrings:account", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("Messaging:RabbitMq:Host", "localhost"),
        new("Messaging:RabbitMq:VirtualHost", "iam"),
        new("Messaging:RabbitMq:UserName", "iam-provisioning"),
        new("Messaging:RabbitMq:Password", "p"),
        new("Messaging:RabbitMq:UseTls", "false"),
    ];

    private static KeyValuePair<string, string?>[] Binding(int index, Guid app, string connector) =>
    [
        new($"Provisioning:Connectors:{index}:ApplicationId", app.ToString("D")),
        new($"Provisioning:Connectors:{index}:ConnectorKey", connector),
        new($"Provisioning:Connectors:{index}:AccountContainer", Container),
        new($"Provisioning:Connectors:{index}:GroupContainer", Groups),
    ];

    private static ItemDetails Details(Guid? account, string? newType, string? name) =>
        new("ben", App, Guid.CreateVersion7(), Guid.CreateVersion7(), account, newType, name);

    private static ApprovalActionRow Action(Guid id, int sequence, string kind, long versionAfter, string payload = "{}") => new()
    {
        InstanceId = id,
        Sequence = sequence,
        Kind = kind,
        Payload = payload,
        AtUtc = Now.AddMinutes(sequence),
        Actor = kind == "Bound" ? null : "owner",
        VersionAfter = versionAfter,
    };

    private static AuditTrailEntry Audit(Guid id, ApprovalActionRow a)
    {
        var kind = Enum.Parse<ApprovalActionKind>(a.Kind);
        return Entry(WorkflowAuditActions.For(kind), "Succeeded", new()
        {
            ["kind"] = a.Kind,
            ["sequence"] = a.Sequence.ToString(CultureInfo.InvariantCulture),
            ["versionAfter"] = a.VersionAfter.ToString(CultureInfo.InvariantCulture),
            ["actionHash"] = ApprovalActionLog.RowHash(id, a.Sequence, new LoggedAction(kind, a.Payload, a.AtUtc, a.Actor), a.VersionAfter),
        });
    }

    private static AuditTrailEntry Entry(string action, string outcome, Dictionary<string, string?> payload) =>
        new(Guid.CreateVersion7(), 1, action, outcome, payload, Intact: true);

    private static Dictionary<string, string?> With(AuditTrailEntry entry, string name, string value) =>
        new(entry.Payload, StringComparer.Ordinal) { [name] = value };

    /// <summary>A connector that ends every operation the same way (only what the adapter calls is implemented).</summary>
    private sealed class FixedConnector(OperationStatus outcome) : IConnector
    {
        public ConnectorCapabilities Capabilities => throw new NotSupportedException();

        public Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult(operation.OperationId, outcome, null));

        public Task<VerificationResult> VerifyAsync(ConnectorOperation operation, CancellationToken cancellationToken) => Task.FromResult(new VerificationResult(true, null));

        public Task<ObjectPage> ListAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
