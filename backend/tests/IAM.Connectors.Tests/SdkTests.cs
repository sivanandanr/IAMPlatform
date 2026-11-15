using IAM.Connectors.Sdk;
using Shouldly;

namespace IAM.Connectors.Tests;

/// <summary>The SDK's own rules (DL-080): the action catalog, capability checks, text rules and what never gets printed.</summary>
public sealed class SdkTests
{
    [Fact]
    public void TheCatalogIsTheSpecsMasterList()
    {
        ActionCatalog.All.Count.ShouldBe(15);
        ActionCatalog.All.Select(a => a.Code).Distinct().Count().ShouldBe(15);
        ActionCatalog.All.GroupBy(a => a.Group).ToDictionary(g => g.Key, g => g.Count()).ShouldBe(new Dictionary<ActionGroup, int>
        {
            [ActionGroup.Accounts] = 6,
            [ActionGroup.EntitlementsOnAnAccount] = 2,
            [ActionGroup.MembersOfAnEntitlement] = 2,
            [ActionGroup.EntitlementDefinitions] = 2,
            [ActionGroup.Credentials] = 3,
        });
        ActionCatalog.All.Where(a => a.Destructive).Select(a => a.Code).ShouldBe([ActionCatalog.AccountDelete, ActionCatalog.EntitlementDelete], ignoreOrder: true);
        ActionCatalog.All.Where(a => a.NeedsSecretReference).Select(a => a.Code).ShouldBe([ActionCatalog.PasswordChange]);
        ActionCatalog.All.ShouldAllBe(a => ConnectorText.IsCode(a.Code, 50));
        ActionCatalog.Find("account.teleport").ShouldBeNull();
        ActionCatalog.Find(null).ShouldBeNull();
    }

    [Fact]
    public void DestructiveActionsAreOffUntilSwitchedOnOneByOne()
    {
        var delete = ActionCatalog.Find(ActionCatalog.AccountDelete)!;
        ConnectorSettings.Default.Allows(delete).ShouldBeFalse();
        ConnectorSettings.Allowing(ActionCatalog.EntitlementDelete).Allows(delete).ShouldBeFalse();
        ConnectorSettings.Allowing(ActionCatalog.AccountDelete).Allows(delete).ShouldBeTrue();
        ConnectorSettings.Default.Allows(ActionCatalog.Find(ActionCatalog.AccountDisable)!).ShouldBeTrue();
    }

    [Fact]
    public void CapabilitiesMustBeConsistent()
    {
        var good = new ConnectorCapabilities(
            "test.good", false, [ActionCatalog.AccountEnable], [new ObjectTypeCapability(ObjectTypes.Account, true, ["displayName"])], 10, 100);
        good.IsValid.ShouldBeTrue();
        ConnectorCapabilities[] bad =
        [
            good with { ConnectorType = "Not A Code" },
            good with { Actions = [] },
            good with { Actions = ["account.teleport"] },
            good with { Actions = [ActionCatalog.AccountEnable, ActionCatalog.AccountEnable] },
            good with { Actions = [ActionCatalog.EntitlementAdd] },
            good with { ObjectTypes = [new ObjectTypeCapability(ObjectTypes.Account, true, ["userPassword"])] },
            good with { ObjectTypes = [new ObjectTypeCapability(ObjectTypes.Account, true, ["bad name"])] },
            good with { MaxOperationsPerSecond = 0 },
            good with { MaxPageSize = ConnectorCapabilities.PageSizeLimit + 1 },
        ];
        bad.ShouldAllBe(c => !c.IsValid);
    }

    [Fact]
    public void TextRulesRefuseWhatCouldSmuggleSomethingIn()
    {
        ConnectorText.IsIdentifier("CN=Ann,OU=Staff,DC=example,DC=com", 512).ShouldBeTrue();
        foreach (var bad in new[] { "", " lead", "trail ", "line\nbreak", "tab\there", "zero​width", "bidi‮override", new string('x', 513) })
        {
            ConnectorText.IsIdentifier(bad, 512).ShouldBeFalse(bad);
        }

        ConnectorText.IsAttributeValue("Ann Example").ShouldBeTrue();
        ConnectorText.IsAttributeValue(null).ShouldBeTrue();
        ConnectorText.IsAttributeValue("a\r\nb").ShouldBeFalse();
        foreach (var name in new[] { "password", "userPassword", "unicodePwd", "clientSecret", "apiKey", "accessToken", "privateKey", "credentials" })
        {
            ConnectorText.LooksLikeCredential(name).ShouldBeTrue(name);
        }

        ConnectorText.LooksLikeCredential("displayName").ShouldBeFalse();
        new SecretReference("customer-vault", "accounts/x").IsValid.ShouldBeTrue();
        new SecretReference("Customer Vault", "x").IsValid.ShouldBeFalse();
        new SecretReference("v", "../../etc").IsValid.ShouldBeFalse();
    }

    [Fact]
    public void NothingPrintsAPersonOrASecret()
    {
        var account = new ObjectRef(ObjectTypes.Account, "CN=Ann Example,OU=Staff");
        var secret = new SecretReference("customer-vault", "people/ann-example");
        var operation = new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.PasswordChange, account, null, new Dictionary<string, string?> { ["displayName"] = "Ann Example" }, secret);
        foreach (var text in new[] { account.ToString(), secret.ToString(), operation.ToString(), new ObjectSnapshot(account, true, false, operation.Attributes, [], null).ToString() })
        {
            text.ShouldNotContain("Ann");
            text.ShouldNotContain("ann-example");
        }
    }

    [Fact]
    public async Task TheLedgerKeepsTheFirstOutcome()
    {
        var ledger = new InMemoryOperationLedger();
        var id = Guid.CreateVersion7();
        await ledger.RecordAsync(new LedgerEntry(id, "a", new OperationResult(id, OperationStatus.Applied, null)), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(new LedgerEntry(id, "b", new OperationResult(id, OperationStatus.Rejected, "x")), TestContext.Current.CancellationToken);
        (await ledger.FindAsync(id, TestContext.Current.CancellationToken)).ShouldNotBeNull().Fingerprint.ShouldBe("a");
        ledger.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ATransientFailureIsReportedAndRetriedNotRemembered()
    {
        var connector = new FlakyConnector();
        var operation = new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.AccountDisable, FlakyConnector.Account, null, new Dictionary<string, string?>(), null);
        (await connector.ExecuteAsync(operation, TestContext.Current.CancellationToken)).ShouldBe(new OperationResult(operation.OperationId, OperationStatus.Failed, ResultCodes.Throttled));
        (await connector.ExecuteAsync(operation, TestContext.Current.CancellationToken)).Status.ShouldBe(OperationStatus.Applied, "the same id runs again after a transient failure");
        (await connector.VerifyAsync(operation, TestContext.Current.CancellationToken)).Matches.ShouldBeTrue();
    }

    /// <summary>Throttled once, then fine.</summary>
    private sealed class FlakyConnector() : ConnectorBase(ConnectorSettings.Default, new InMemoryOperationLedger())
    {
        public static readonly ObjectRef Account = new(ObjectTypes.Account, "flaky");
        private bool _failed;
        private bool _enabled = true;

        public override ConnectorCapabilities Capabilities { get; } = new(
            "test.flaky", false, [ActionCatalog.AccountDisable], [new ObjectTypeCapability(ObjectTypes.Account, false, ["displayName"])], 10, 1);

        public override Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectorHealth(HealthStatus.Healthy, null, false));

        protected override Task<ObjectSnapshot?> ReadCoreAsync(ObjectRef target, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectSnapshot?>(target == Account ? new ObjectSnapshot(Account, _enabled, false, new Dictionary<string, string?>(), [], null) : null);

        protected override Task<OperationResult> ApplyAsync(ConnectorOperation operation, ObjectSnapshot? current, CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                throw new ConnectorTransientException(ResultCodes.Throttled);
            }

            _enabled = false;
            return Task.FromResult(OperationResult.Of(operation, OperationStatus.Applied));
        }

        protected override Task<ObjectPage> ListCoreAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectPage([], null));
    }
}
