using System.Globalization;
using IAM.Connectors.Sdk;
using IAM.Modules.Audit.Contracts;
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
        var binding = ConnectorBinding.Create(App, "mock-ad", Container);
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

        Should.Throw<ArgumentException>(() => ProvisioningPlanner.ForNewAccount(item, 3, Details(null, "Secondary", "adm.ben"), Group, ConnectorBinding.Create(Guid.CreateVersion7(), "mock-ad", Container), Now));
    }

    [Fact]
    public void AnExistingAccountIsOnlyGivenTheEntitlement()
    {
        var item = Guid.CreateVersion7();
        var plan = ProvisioningPlanner.ForExistingAccount(item, 2, Details(Guid.CreateVersion7(), null, null), $"CN=ben,{Container}", Group, ConnectorBinding.Create(App, "mock-ad", Container), Now);
        plan.Operations.Select(o => (o.Step, o.Action, o.TargetId, o.RelatedId)).ShouldBe([(1, "account.entitlement.add", $"CN=ben,{Container}", Group)]);
        Should.Throw<ArgumentException>(() => ProvisioningPlanner.ForExistingAccount(item, 2, Details(null, "Secondary", "x"), "CN=x", Group, ConnectorBinding.Create(App, "mock-ad", Container), Now));
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
        Should.Throw<ArgumentException>(() => ConnectorBinding.Create(App, connector, container));

    [Fact]
    public void ABindingNeedsItsApplication() =>
        Should.Throw<ArgumentException>(() => ConnectorBinding.Create(Guid.Empty, "mock-ad", Container));

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
            host.Services.GetServices<IHostedService>().Count().ShouldBe(1, "the consumer");
            host.Services.GetRequiredService<IOperationLedger>().ShouldBeOfType<DurableOperationLedger>();
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
        bindings.ShouldHaveSingleItem().ShouldBe(new ConnectorBinding(App, "mock-ad", Container));
        ProvisioningWorkerHost.Bindings(new ConfigurationBuilder().Build()).ShouldBeEmpty("no allowlist: everything is manual work");
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
}
