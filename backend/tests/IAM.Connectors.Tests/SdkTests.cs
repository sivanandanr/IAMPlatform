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
        ActionCatalog.All.Where(a => a.Gated).Select(a => a.Code)
            .ShouldBe([ActionCatalog.AccountDelete, ActionCatalog.EntitlementDelete, ActionCatalog.PasswordChange, ActionCatalog.PasswordReset], ignoreOrder: true);
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
    public void SettingsAreCheckedAndCanNotBeChangedLater()
    {
        var codes = new List<string> { ActionCatalog.AccountDelete };
        var settings = new ConnectorSettings(codes);
        codes.Add(ActionCatalog.EntitlementDelete);
        settings.Allows(ActionCatalog.Find(ActionCatalog.EntitlementDelete)!).ShouldBeFalse("the list was copied when the settings were made");
        Should.Throw<ArgumentException>(() => ConnectorSettings.Allowing("account.teleport"));
        Should.Throw<ArgumentException>(() => ConnectorSettings.Allowing(ActionCatalog.AccountDisable));
        Should.Throw<ArgumentOutOfRangeException>(() => ConnectorSettings.Default with { MaxOperationsPerSecond = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => ConnectorSettings.Default with { MaxOperationsPerSecond = 10_001 });
        Should.Throw<ArgumentException>(() => ConnectorSettings.Default with { SecretVault = "Bad Vault" });
        Should.Throw<ArgumentException>(() => ConnectorSettings.Default with { SecretPathPrefix = "no-slash" });
        Should.Throw<ArgumentException>(() => ConnectorSettings.Default with { SecretPathPrefix = "../" });
        ConnectorSettings.Default.AllowsSecret(new SecretReference("v", "a/b")).ShouldBeFalse("nothing is allowed until a vault and path are set");
        var allowed = ConnectorSettings.Default with { SecretVault = "v", SecretPathPrefix = "a/" };
        allowed.AllowsSecret(new SecretReference("v", "a/b")).ShouldBeTrue();
        allowed.AllowsSecret(new SecretReference("v", "ab")).ShouldBeFalse();
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
            good with { ObjectTypes = [new ObjectTypeCapability(ObjectTypes.Account, true, ["userAccountControl"])] },
            good with { ObjectTypes = [new ObjectTypeCapability(ObjectTypes.Account, true, ["SERVICEPRINCIPALNAME"])] },
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
        foreach (var bad in new[] { "a/../b", "/a", "a//b", "a/", ".hidden", "a/.b" })
        {
            new SecretReference("v", bad).IsValid.ShouldBeFalse(bad);
        }

        ConnectorText.IsForbiddenAttribute("UserAccountControl").ShouldBeTrue("compared ignoring case");
        ConnectorText.IsForbiddenAttribute("msDS-AllowedToActOnBehalfOfOtherIdentity").ShouldBeTrue();
        ConnectorText.IsForbiddenAttribute("department").ShouldBeFalse();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheSdkSaysAWriteWasRateLimited(bool returns)
    {
        // DL-104: RateLimited means "refused before the connector ran"; a connector that ran may have written, so it can't claim
        // it, whether it throws it or returns it.
        var connector = new FlakyConnector(ResultCodes.RateLimited, returns);
        var operation = new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.AccountDisable, FlakyConnector.Account, null, new Dictionary<string, string?>(), null);
        (await connector.ExecuteAsync(operation, TestContext.Current.CancellationToken)).ShouldBe(new OperationResult(operation.OperationId, OperationStatus.Failed, ResultCodes.ConnectorError));
    }

    [Fact]
    public async Task AConnectorsOwnErrorIsReportedNeverThrown()
    {
        var connector = new FaultyConnector(faultyRead: false);
        var operation = new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.AccountDisable, FaultyConnector.Account, null, new Dictionary<string, string?>(), null);
        (await connector.ExecuteAsync(operation, TestContext.Current.CancellationToken)).ShouldBe(new OperationResult(operation.OperationId, OperationStatus.Failed, ResultCodes.ConnectorError));
    }

    [Fact]
    public async Task WhatAConnectorReturnsIsCheckedAndFiltered()
    {
        var connector = new FaultyConnector(faultyRead: true);
        var snapshot = (await connector.ReadAsync(FaultyConnector.Account, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        snapshot.Attributes.Keys.ShouldBe(["displayName"], "undeclared, privilege and credential attributes are cut away");

        var broken = new FaultyConnector(faultyRead: true) { BadLink = true };
        await Should.ThrowAsync<ConnectorTransientException>(() => broken.ReadAsync(FaultyConnector.Account, TestContext.Current.CancellationToken));
        var operation = new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.AccountDisable, FaultyConnector.Account, null, new Dictionary<string, string?>(), null);
        (await broken.ExecuteAsync(operation, TestContext.Current.CancellationToken)).Code.ShouldBe(ResultCodes.InvalidResponse);
    }

    [Fact]
    public async Task LocksAreFreedAfterUseAndAfterCancellation()
    {
        var ledger = new InMemoryOperationLedger();
        var connector = new MinimalTestConnector(ConnectorSettings.Default, ledger);
        var ops = Enumerable.Range(0, 40).Select(i => new ConnectorOperation(
            Guid.CreateVersion7(), ActionCatalog.AccountCreate, new ObjectRef(ObjectTypes.Account, $"user-{i}"), MinimalTestConnector.Container,
            new Dictionary<string, string?> { ["displayName"] = "x" }, null)).ToList();
        await Task.WhenAll(ops.Select(o => connector.ExecuteAsync(o, TestContext.Current.CancellationToken)));
        connector.ActiveGates.ShouldBe(0, "every lock is removed when its last user leaves");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => connector.ExecuteAsync(ops[0] with { OperationId = Guid.CreateVersion7() }, cancelled.Token));
        connector.ActiveGates.ShouldBe(0);
        (await connector.ExecuteAsync(ops[0], TestContext.Current.CancellationToken)).Status.ShouldBe(OperationStatus.Applied, "nothing was left locked");
    }

    [Fact]
    public async Task ASuccessThatSaysRateLimitedIsAnInvalidResponseNeverRemembered()
    {
        // DL-105: a connector can't claim the SDK's code even on a success; whether it wrote is unknown, so it isn't kept.
        var connector = new FlakyConnector(ResultCodes.RateLimited, returns: true, status: OperationStatus.Applied);
        var operation = new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.AccountDisable, FlakyConnector.Account, null, new Dictionary<string, string?>(), null);
        (await connector.ExecuteAsync(operation, TestContext.Current.CancellationToken)).ShouldBe(new OperationResult(operation.OperationId, OperationStatus.Failed, ResultCodes.InvalidResponse));
        (await connector.ExecuteAsync(operation, TestContext.Current.CancellationToken)).Status.ShouldBe(OperationStatus.Applied, "nothing was remembered, so the same id runs again");
    }

    [Fact]
    public async Task AReadTheConnectorSaysWasRateLimitedIsItsOwnFault()
    {
        // DL-105: on a read too, RateLimited is the SDK's alone; a connector's own is a fault worth retrying.
        var connector = new FlakyConnector(ResultCodes.RateLimited, readFails: true);
        var operation = new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.AccountDisable, FlakyConnector.Account, null, new Dictionary<string, string?>(), null);
        (await connector.VerifyAsync(operation, TestContext.Current.CancellationToken)).ShouldBe(new VerificationResult(false, ResultCodes.ConnectorError));
    }

    /// <summary>Throws from its writes, or returns snapshots with things it must not.</summary>
    private sealed class FaultyConnector(bool faultyRead) : ConnectorBase(ConnectorSettings.Default, new InMemoryOperationLedger())
    {
        public static readonly ObjectRef Account = new(ObjectTypes.Account, "faulty");

        public bool BadLink { get; init; }

        public override ConnectorCapabilities Capabilities { get; } = new(
            "test.faulty", false, [ActionCatalog.AccountDisable], [new ObjectTypeCapability(ObjectTypes.Account, false, ["displayName"])], 10, 1);

        public override Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectorHealth(HealthStatus.Healthy, null, false));

        protected override Task<ObjectSnapshot?> ReadCoreAsync(ObjectRef target, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectSnapshot?>(new ObjectSnapshot(
                Account,
                true,
                false,
                faultyRead
                    ? new Dictionary<string, string?> { ["displayName"] = "Ann", ["unicodePwd"] = "secret", ["userAccountControl"] = "512", ["notDeclared"] = "x" }
                    : new Dictionary<string, string?>(),
                BadLink ? [new ObjectRef(ObjectTypes.Entitlement, "bad\nlink")] : [],
                null));

        protected override Task<OperationResult> ApplyAsync(ConnectorOperation operation, ObjectSnapshot? current, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the connector's own bug");

        protected override Task<ObjectPage> ListCoreAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectPage([], null));
    }

    /// <summary>
    /// Fails once with a code (thrown, or returned with a status), then fine. With <c>readFails</c>, its reads throw the code
    /// instead.
    /// </summary>
    private sealed class FlakyConnector(string code = ResultCodes.Throttled, bool returns = false, OperationStatus status = OperationStatus.Failed, bool readFails = false)
        : ConnectorBase(ConnectorSettings.Default, new InMemoryOperationLedger())
    {
        public static readonly ObjectRef Account = new(ObjectTypes.Account, "flaky");
        private bool _failed;
        private bool _enabled = true;

        public override ConnectorCapabilities Capabilities { get; } = new(
            "test.flaky", false, [ActionCatalog.AccountDisable], [new ObjectTypeCapability(ObjectTypes.Account, false, ["displayName"])], 10, 1);

        public override Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectorHealth(HealthStatus.Healthy, null, false));

        protected override Task<ObjectSnapshot?> ReadCoreAsync(ObjectRef target, CancellationToken cancellationToken) =>
            readFails
                ? throw new ConnectorTransientException(code)
                : Task.FromResult<ObjectSnapshot?>(target == Account ? new ObjectSnapshot(Account, _enabled, false, new Dictionary<string, string?>(), [], null) : null);

        protected override Task<OperationResult> ApplyAsync(ConnectorOperation operation, ObjectSnapshot? current, CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                return returns
                    ? Task.FromResult(OperationResult.Of(operation, status, code))
                    : throw new ConnectorTransientException(code);
            }

            _enabled = false;
            return Task.FromResult(OperationResult.Of(operation, OperationStatus.Applied));
        }

        protected override Task<ObjectPage> ListCoreAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectPage([], null));
    }
}
