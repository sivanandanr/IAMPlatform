using IAM.Connectors.Sdk;
using Shouldly;

namespace IAM.Connectors.Tests;

/// <summary>
/// The contract every connector must pass (P0-011, ADR-0007, DL-080). A connector's own test class derives from this and
/// says how to build one and which objects already exist; tests of actions the connector doesn't support are skipped.
/// Passing proves the safety rules, not that a connector is production-ready: that needs end-to-end verification against
/// the real target (FR-REL-103).
/// </summary>
public abstract class ConnectorContractTests
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh connector with an empty directory apart from <see cref="Container"/> and <see cref="ExistingEntitlement"/>.</summary>
    protected abstract IConnector Create(ConnectorSettings settings);

    /// <summary>A container accounts and entitlements are created in.</summary>
    protected abstract ObjectRef Container { get; }

    /// <summary>An entitlement that exists in every fresh connector.</summary>
    protected abstract ObjectRef ExistingEntitlement { get; }

    /// <summary>A new, valid account identifier the connector hasn't seen.</summary>
    protected abstract ObjectRef NewAccount();

    /// <summary>An identifier with the right shape for the SDK but wrong for this connector's own syntax, if it has one.</summary>
    protected virtual ObjectRef? MalformedAccount => null;

    private static ConnectorOperation Op(string action, ObjectRef target, ObjectRef? related = null, IReadOnlyDictionary<string, string?>? attributes = null, SecretReference? secret = null, Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), action, target, related, attributes ?? new Dictionary<string, string?>(), secret);

    private static string AccountAttribute(IConnector connector) => connector.Capabilities.TypeOf(ObjectTypes.Account)!.Attributes[0];

    private async Task<ObjectRef> CreatedAccountAsync(IConnector connector)
    {
        Assert.SkipUnless(connector.Capabilities.Supports(ActionCatalog.AccountCreate), "creates no accounts");
        var account = NewAccount();
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountCreate, account, Container, new Dictionary<string, string?> { [AccountAttribute(connector)] = "Created" }), Ct))
            .Status.ShouldBe(OperationStatus.Applied);
        return account;
    }

    private static void SkipUnless(IConnector connector, string action) =>
        Assert.SkipUnless(connector.Capabilities.Supports(action), $"{connector.Capabilities.ConnectorType} doesn't support {action}");

    [Fact]
    public async Task ItDeclaresWhatItCanDoHonestly()
    {
        var connector = Create(ConnectorSettings.Default);
        connector.Capabilities.IsValid.ShouldBeTrue();
        connector.Capabilities.TypeOf(ObjectTypes.Account).ShouldNotBeNull().Attributes.ShouldNotBeEmpty();
        var health = await connector.CheckHealthAsync(Ct);
        health.ProductionReady.ShouldBe(connector.Capabilities.ProductionReady, "health and capabilities tell the same story");
    }

    [Fact]
    public async Task ARepeatedOperationIdChangesNothingTwice()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountUpdate);
        var account = await CreatedAccountAsync(connector);
        var attribute = AccountAttribute(connector);
        var first = Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { [attribute] = "First" });
        (await connector.ExecuteAsync(first, Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { [attribute] = "Second" }), Ct)).Status.ShouldBe(OperationStatus.Applied);

        // The first operation sent again: the same answer, and the later change stands.
        (await connector.ExecuteAsync(first, Ct)).ShouldBe(new OperationResult(first.OperationId, OperationStatus.Applied, null));
        (await connector.ReadAsync(account, Ct)).ShouldNotBeNull().Attributes[attribute].ShouldBe("Second");
    }

    [Fact]
    public async Task AnOperationIdReusedForSomethingElseIsRefused()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountDisable);
        var account = await CreatedAccountAsync(connector);
        var id = Guid.CreateVersion7();
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountDisable, account, id: id), Ct)).Status.ShouldBe(OperationStatus.Applied);
        var reused = await connector.ExecuteAsync(Op(ActionCatalog.AccountDisable, await CreatedAccountAsync(connector), id: id), Ct);
        reused.ShouldBe(new OperationResult(id, OperationStatus.Rejected, ResultCodes.OperationIdReused));
    }

    [Fact]
    public async Task ItReadsBeforeItWrites()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountEnable);
        var account = await CreatedAccountAsync(connector);
        (await connector.ReadAsync(account, Ct)).ShouldNotBeNull().Enabled.ShouldBe(true);
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountEnable, account), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);

        // Creating the same account again with the same values is already done; with other values, refused.
        var attribute = AccountAttribute(connector);
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountCreate, account, Container, new Dictionary<string, string?> { [attribute] = "Created" }), Ct))
            .Status.ShouldBe(OperationStatus.AlreadyInState);
        var other = Op(ActionCatalog.AccountCreate, account, Container, new Dictionary<string, string?> { [attribute] = "Someone else" });
        (await connector.ExecuteAsync(other, Ct)).ShouldBe(new OperationResult(other.OperationId, OperationStatus.Rejected, ResultCodes.AlreadyExistsDifferently));
        (await connector.ReadAsync(account, Ct)).ShouldNotBeNull().Attributes[attribute].ShouldBe("Created");
    }

    [Fact]
    public async Task UnknownAndUnsupportedActionsAreSaidSo()
    {
        var connector = Create(ConnectorSettings.Default);
        var account = NewAccount();
        (await connector.ExecuteAsync(Op("account.teleport", account), Ct)).Code.ShouldBe(ResultCodes.UnknownAction);
        var unsupported = ActionCatalog.All.FirstOrDefault(a => !connector.Capabilities.Supports(a.Code));
        Assert.SkipWhen(unsupported is null, "supports the whole catalog");
        var result = await connector.ExecuteAsync(Op(unsupported!.Code, new ObjectRef(unsupported.Target, "x"), unsupported.Related is { } r ? new ObjectRef(r, "y") : null), Ct);
        result.Status.ShouldBe(OperationStatus.Unsupported);
        result.Code.ShouldBe(ResultCodes.NotSupported);
    }

    [Fact]
    public async Task DestructiveActionsStartSwitchedOff()
    {
        var off = Create(ConnectorSettings.Default);
        SkipUnless(off, ActionCatalog.AccountDelete);
        var account = await CreatedAccountAsync(off);
        (await off.ExecuteAsync(Op(ActionCatalog.AccountDelete, account), Ct)).Code.ShouldBe(ResultCodes.ActionNotAllowed);
        (await off.ReadAsync(account, Ct)).ShouldNotBeNull();

        var on = Create(ConnectorSettings.Allowing(ActionCatalog.AccountDelete));
        var doomed = await CreatedAccountAsync(on);
        (await on.ExecuteAsync(Op(ActionCatalog.AccountDelete, doomed), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await on.ReadAsync(doomed, Ct)).ShouldBeNull();
        (await on.ExecuteAsync(Op(ActionCatalog.AccountDelete, doomed), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);
    }

    [Fact]
    public async Task BadRequestsAreRefusedAndChangeNothing()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountUpdate);
        var account = await CreatedAccountAsync(connector);
        var attribute = AccountAttribute(connector);
        var before = await connector.ReadAsync(account, Ct);

        (string Code, ConnectorOperation Operation)[] refused =
        [
            (ResultCodes.InvalidOperation, Op(ActionCatalog.AccountUpdate, new ObjectRef(ObjectTypes.Account, "bad\nid"), attributes: new Dictionary<string, string?> { [attribute] = "x" })),
            (ResultCodes.InvalidOperation, Op(ActionCatalog.AccountUpdate, new ObjectRef(ObjectTypes.Account, new string('a', ObjectRef.MaxIdLength + 1)), attributes: new Dictionary<string, string?> { [attribute] = "x" })),
            (ResultCodes.InvalidOperation, Op(ActionCatalog.AccountUpdate, new ObjectRef(ObjectTypes.Entitlement, account.Id), attributes: new Dictionary<string, string?> { [attribute] = "x" })),
            (ResultCodes.InvalidOperation, Op(ActionCatalog.AccountUpdate, account, ExistingEntitlement, new Dictionary<string, string?> { [attribute] = "x" })),
            (ResultCodes.InvalidOperation, Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { [attribute] = "x" }, id: Guid.Empty)),
            (ResultCodes.AttributeNotAllowed, Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { ["notDeclaredAnywhere"] = "x" })),
            (ResultCodes.AttributeNotAllowed, Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { [attribute] = "line\nbreak" })),
            (ResultCodes.AttributeNotAllowed, Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { [attribute] = new string('v', ConnectorOperation.MaxAttributeValueLength + 1) })),
            (ResultCodes.AttributeNotAllowed, Op(ActionCatalog.AccountUpdate, account, attributes: Enumerable.Range(0, ConnectorOperation.MaxAttributes + 1).ToDictionary(i => $"a{i}", _ => (string?)"x"))),
            (ResultCodes.CredentialInAttributes, Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { ["userPassword"] = "hunter2" })),
        ];
        if (connector.Capabilities.Supports(ActionCatalog.AccountEnable))
        {
            refused = [.. refused, (ResultCodes.AttributeNotAllowed, Op(ActionCatalog.AccountEnable, account, attributes: new Dictionary<string, string?> { [attribute] = "x" }))];
        }

        if (MalformedAccount is { } malformed)
        {
            refused = [.. refused, (ResultCodes.InvalidOperation, Op(ActionCatalog.AccountUpdate, malformed, attributes: new Dictionary<string, string?> { [attribute] = "x" }))];
        }

        foreach (var (code, operation) in refused)
        {
            var result = await connector.ExecuteAsync(operation, Ct);
            result.Status.ShouldBe(OperationStatus.Rejected, code);
            result.Code.ShouldBe(code);
        }

        (await connector.ReadAsync(account, Ct)).ShouldBeEquivalentTo(before);
    }

    [Fact]
    public async Task PasswordsTravelOnlyAsVaultReferences()
    {
        var connector = Create(ConnectorSettings.Default);
        var account = await CreatedAccountAsync(connector);
        var reference = new SecretReference("customer-vault", "accounts/new-password");
        if (connector.Capabilities.Supports(ActionCatalog.PasswordChange))
        {
            (await connector.ExecuteAsync(Op(ActionCatalog.PasswordChange, account), Ct)).Code.ShouldBe(ResultCodes.SecretReferenceRequired);
            (await connector.ExecuteAsync(Op(ActionCatalog.PasswordChange, account, secret: new SecretReference("Bad Vault", "x")), Ct)).Code.ShouldBe(ResultCodes.SecretReferenceRequired);
            var changed = await connector.ExecuteAsync(Op(ActionCatalog.PasswordChange, account, secret: reference), Ct);
            changed.Status.ShouldBe(OperationStatus.Applied);
            changed.ToString().ShouldNotContain(reference.Name);
        }

        if (connector.Capabilities.Supports(ActionCatalog.AccountDisable))
        {
            (await connector.ExecuteAsync(Op(ActionCatalog.AccountDisable, account, secret: reference), Ct)).Code.ShouldBe(ResultCodes.SecretReferenceNotAllowed);
        }

        var snapshot = (await connector.ReadAsync(account, Ct)).ShouldNotBeNull();
        snapshot.Attributes.Keys.ShouldAllBe(k => !ConnectorText.LooksLikeCredential(k), "a snapshot never carries a credential");
        snapshot.Attributes.Values.ShouldAllBe(v => v == null || !v.Contains(reference.Name, StringComparison.Ordinal));
        reference.ToString().ShouldNotContain(reference.Name);
    }

    [Fact]
    public async Task MissingTargetsAndRelatedObjectsAreRefused()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountDisable);
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountDisable, NewAccount()), Ct)).Code.ShouldBe(ResultCodes.TargetNotFound);
        if (connector.Capabilities.Supports(ActionCatalog.EntitlementAdd))
        {
            var account = await CreatedAccountAsync(connector);
            (await connector.ExecuteAsync(Op(ActionCatalog.EntitlementAdd, account, new ObjectRef(ObjectTypes.Entitlement, ExistingEntitlement.Id + "-missing")), Ct))
                .Code.ShouldBe(ResultCodes.RelatedNotFound);
        }
    }

    [Fact]
    public async Task EntitlementsAreAddedOnceAndVerified()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.EntitlementAdd);
        var account = await CreatedAccountAsync(connector);
        var add = Op(ActionCatalog.EntitlementAdd, account, ExistingEntitlement);
        (await connector.VerifyAsync(add, Ct)).Matches.ShouldBeFalse();

        // Many at once, some with the same operation id: one change, the same answer for that id.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => connector.ExecuteAsync(i % 2 == 0 ? add : add with { OperationId = Guid.CreateVersion7() }, Ct)));
        results.Where(r => r.Status == OperationStatus.Applied).Select(r => r.OperationId).Distinct().Count().ShouldBe(1, "one operation made the change; its repeats got its answer");
        results.ShouldAllBe(r => r.Status == OperationStatus.Applied || r.Status == OperationStatus.AlreadyInState);
        (await connector.ReadAsync(account, Ct)).ShouldNotBeNull().Links.Count(l => l == ExistingEntitlement).ShouldBe(1);
        (await connector.VerifyAsync(add, Ct)).Matches.ShouldBeTrue();

        if (connector.Capabilities.Supports(ActionCatalog.EntitlementRemove))
        {
            var remove = Op(ActionCatalog.EntitlementRemove, account, ExistingEntitlement);
            (await connector.ExecuteAsync(remove, Ct)).Status.ShouldBe(OperationStatus.Applied);
            (await connector.ExecuteAsync(remove with { OperationId = Guid.CreateVersion7() }, Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);
            (await connector.VerifyAsync(remove, Ct)).Matches.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task ListingPagesThroughEverythingOnce()
    {
        var connector = Create(ConnectorSettings.Default);
        Assert.SkipUnless(connector.Capabilities.TypeOf(ObjectTypes.Account) is { Discoverable: true }, "can't discover accounts");
        var created = new List<ObjectRef>();
        for (var i = 0; i < 5; i++)
        {
            created.Add(await CreatedAccountAsync(connector));
        }

        var seen = new List<ObjectRef>();
        string? continuation = null;
        var pages = 0;
        do
        {
            var page = await connector.ListAsync(ObjectTypes.Account, continuation, 2, Ct);
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(i => i.Ref));
            continuation = page.Continuation;
            (++pages).ShouldBeLessThan(100, "the listing must end");
        }
        while (continuation is not null);

        seen.Distinct().Count().ShouldBe(seen.Count, "no object twice");
        created.ShouldAllBe(c => seen.Contains(c));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => connector.ListAsync(ObjectTypes.Account, null, connector.Capabilities.MaxPageSize + 1, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => connector.ListAsync(ObjectTypes.Account, null, 0, Ct));
        await Should.ThrowAsync<ArgumentException>(() => connector.ListAsync("nothing.like.this", null, 1, Ct));
    }
}
