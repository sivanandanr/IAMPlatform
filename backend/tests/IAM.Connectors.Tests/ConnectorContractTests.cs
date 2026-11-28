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

    /// <summary>Settings that switch password changes on, with the vault and path the suite's references use.</summary>
    protected static ConnectorSettings PasswordSettings { get; } =
        ConnectorSettings.Allowing(ActionCatalog.PasswordChange, ActionCatalog.PasswordReset) with { SecretVault = "customer-vault", SecretPathPrefix = "accounts/" };

    /// <summary>Whether an account and an entitlement can't share a name, as in a directory (one namespace for all objects).</summary>
    protected virtual bool OneNamespace => false;

    /// <summary>A valid entitlement identifier that doesn't exist.</summary>
    protected virtual ObjectRef AbsentEntitlement => new(ObjectTypes.Entitlement, ExistingEntitlement.Id + "-missing");

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

        // The first operation sent again: the same answer, marked as the ledger's (DL-109), and the later change stands.
        (await connector.ExecuteAsync(first, Ct)).ShouldBe(new OperationResult(first.OperationId, OperationStatus.Applied, null) { Replayed = true });
        (await connector.ReadAsync(account, Ct)).ShouldNotBeNull().Attributes[attribute].ShouldBe("Second");
    }

    [Fact]
    public async Task AnOperationIdReusedForSomethingElseIsRefused()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountDisable);
        var account = await CreatedAccountAsync(connector);
        if (connector.Capabilities.Supports(ActionCatalog.AccountEnable))
        {
            await connector.ExecuteAsync(Op(ActionCatalog.AccountEnable, account), Ct);
        }

        var id = Guid.CreateVersion7();
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountDisable, account, id: id), Ct)).Status.ShouldBeOneOf(OperationStatus.Applied, OperationStatus.AlreadyInState);
        var reused = await connector.ExecuteAsync(Op(ActionCatalog.AccountDisable, await CreatedAccountAsync(connector), id: id), Ct);
        reused.ShouldBe(new OperationResult(id, OperationStatus.Rejected, ResultCodes.OperationIdReused));
    }

    [Fact]
    public async Task ItReadsBeforeItWrites()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountEnable);
        var account = await CreatedAccountAsync(connector);

        // Whether a new account starts enabled is the target's business; enabling twice changes it once.
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountEnable, account), Ct)).Status.ShouldBeOneOf(OperationStatus.Applied, OperationStatus.AlreadyInState);
        (await connector.ReadAsync(account, Ct)).ShouldNotBeNull().Enabled.ShouldBe(true);
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountEnable, account), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);

        // A create never takes over an account that exists, even with the same values or none (DL-083a).
        var attribute = AccountAttribute(connector);
        foreach (var attributes in new[] { new Dictionary<string, string?> { [attribute] = "Created" }, [], new Dictionary<string, string?> { [attribute] = "Someone else" } })
        {
            var again = Op(ActionCatalog.AccountCreate, account, Container, attributes);
            (await connector.ExecuteAsync(again, Ct)).ShouldBe(new OperationResult(again.OperationId, OperationStatus.Rejected, ResultCodes.AlreadyExists));
        }

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
        var connector = Create(PasswordSettings);
        var account = await CreatedAccountAsync(connector);
        var reference = new SecretReference("customer-vault", "accounts/new-password");
        if (connector.Capabilities.Supports(ActionCatalog.PasswordChange))
        {
            // Off unless switched on (DL-083d).
            var off = Create(ConnectorSettings.Default);
            var someone = await CreatedAccountAsync(off);
            (await off.ExecuteAsync(Op(ActionCatalog.PasswordChange, someone, secret: reference), Ct)).Code.ShouldBe(ResultCodes.ActionNotAllowed);

            (await connector.ExecuteAsync(Op(ActionCatalog.PasswordChange, account), Ct)).Code.ShouldBe(ResultCodes.SecretReferenceRequired);
            foreach (var bad in new[] { "accounts/../admin", "/accounts/x", "accounts//x", "accounts/./x" })
            {
                (await connector.ExecuteAsync(Op(ActionCatalog.PasswordChange, account, secret: new SecretReference("customer-vault", bad)), Ct)).Code
                    .ShouldBe(ResultCodes.SecretReferenceRequired, bad);
            }

            (await connector.ExecuteAsync(Op(ActionCatalog.PasswordChange, account, secret: new SecretReference("other-vault", "accounts/x")), Ct)).Code
                .ShouldBe(ResultCodes.SecretOutsideAllowlist);
            (await connector.ExecuteAsync(Op(ActionCatalog.PasswordChange, account, secret: new SecretReference("customer-vault", "admins/x")), Ct)).Code
                .ShouldBe(ResultCodes.SecretOutsideAllowlist);
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
        snapshot.Attributes.Keys.ShouldAllBe(k => !ConnectorText.IsForbiddenAttribute(k), "a snapshot never carries a credential or privilege attribute");
        snapshot.Attributes.Values.ShouldAllBe(v => v == null || !v.Contains(reference.Name, StringComparison.Ordinal));
        reference.ToString().ShouldNotContain(reference.Name);
    }

    [Fact]
    public async Task PrivilegeAndCredentialAttributesAreRefused()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountUpdate);
        var account = await CreatedAccountAsync(connector);
        foreach (var name in new[] { "userAccountControl", "servicePrincipalName", "sIDHistory", "member", "scriptPath", "userPKCS12", "msDS-KeyCredentialLink" })
        {
            var update = Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { [name] = "x" });
            (await connector.ExecuteAsync(update, Ct)).Code.ShouldBeOneOf([ResultCodes.ForbiddenAttribute, ResultCodes.CredentialInAttributes], name);
        }

        connector.Capabilities.ObjectTypes.SelectMany(t => t.Attributes).ShouldAllBe(a => !ConnectorText.IsForbiddenAttribute(a));
    }

    [Fact]
    public async Task AMissingAttributeMapIsRefusedNotThrown()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountUpdate);
        var account = await CreatedAccountAsync(connector);
        var broken = new ConnectorOperation(Guid.CreateVersion7(), ActionCatalog.AccountUpdate, account, null, null!, null);
        (await connector.ExecuteAsync(broken, Ct)).Code.ShouldBe(ResultCodes.InvalidOperation);
        (await connector.VerifyAsync(broken, Ct)).Matches.ShouldBeFalse();
    }

    [Fact]
    public async Task ClearingAnAttributeIsDoneOnceItIsGone()
    {
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.AccountUpdate);
        var account = await CreatedAccountAsync(connector);
        var clear = Op(ActionCatalog.AccountUpdate, account, attributes: new Dictionary<string, string?> { [AccountAttribute(connector)] = null });
        (await connector.ExecuteAsync(clear, Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await connector.VerifyAsync(clear, Ct)).Matches.ShouldBeTrue();
        (await connector.ExecuteAsync(clear with { OperationId = Guid.CreateVersion7() }, Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);
    }

    [Fact]
    public async Task TakingAccessAwayFromSomethingGoneIsAlreadyDone()
    {
        // A leaver's account or a deleted group: the access is gone, which is what was asked (DL-083).
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.EntitlementRemove);
        var account = await CreatedAccountAsync(connector);
        (await connector.ExecuteAsync(Op(ActionCatalog.EntitlementRemove, account, AbsentEntitlement), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);
        (await connector.ExecuteAsync(Op(ActionCatalog.EntitlementRemove, NewAccount(), ExistingEntitlement), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);
    }

    [Fact]
    public async Task AFailureThatDependsOnTimingIsNotRemembered()
    {
        // An entitlement for an account that doesn't exist yet: refused now, done under the same id once it does.
        var connector = Create(ConnectorSettings.Default);
        SkipUnless(connector, ActionCatalog.EntitlementAdd);
        Assert.SkipUnless(connector.Capabilities.Supports(ActionCatalog.AccountCreate), "creates no accounts");
        var account = NewAccount();
        var add = Op(ActionCatalog.EntitlementAdd, account, ExistingEntitlement);
        (await connector.ExecuteAsync(add, Ct)).Code.ShouldBe(ResultCodes.TargetNotFound);
        (await connector.ExecuteAsync(Op(ActionCatalog.AccountCreate, account, Container, new Dictionary<string, string?> { [AccountAttribute(connector)] = "Late" }), Ct))
            .Status.ShouldBe(OperationStatus.Applied);
        (await connector.ExecuteAsync(add, Ct)).Status.ShouldBe(OperationStatus.Applied);
    }

    [Fact]
    public async Task AnotherSpellingOfTheSameNameIsTheSameObject()
    {
        var connector = Create(ConnectorSettings.Default);
        Assert.SkipUnless(connector.Capabilities.IdsIgnoreCase, "names compare exactly");
        SkipUnless(connector, ActionCatalog.EntitlementRemove);
        var account = await CreatedAccountAsync(connector);
        (await connector.ExecuteAsync(Op(ActionCatalog.EntitlementAdd, account, ExistingEntitlement), Ct)).Status.ShouldBe(OperationStatus.Applied);

        // Removing under another spelling really removes it: never "already done" while the access stays (DL-083b).
        var shouted = new ObjectRef(ObjectTypes.Entitlement, ExistingEntitlement.Id.ToUpperInvariant());
        var remove = Op(ActionCatalog.EntitlementRemove, account, shouted);
        (await connector.ExecuteAsync(remove, Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await connector.ReadAsync(account, Ct)).ShouldNotBeNull().Links.ShouldBeEmpty();
        (await connector.VerifyAsync(remove, Ct)).Matches.ShouldBeTrue();
        (await connector.ReadAsync(new ObjectRef(ObjectTypes.Account, account.Id.ToLowerInvariant().Replace("cn=", "CN=", StringComparison.Ordinal).Replace("ou=", "OU=", StringComparison.Ordinal).Replace("dc=", "DC=", StringComparison.Ordinal)), Ct))
            .ShouldNotBeNull().Ref.Id.ShouldBe(account.Id, "an object keeps the spelling it was created with");
    }

    [Fact]
    public async Task ACreateCanNotTakeANameUsedByAnotherType()
    {
        var connector = Create(ConnectorSettings.Default);
        Assert.SkipUnless(OneNamespace, "accounts and entitlements have separate names");
        Assert.SkipUnless(connector.Capabilities.Supports(ActionCatalog.AccountCreate), "creates no accounts");
        var clash = Op(ActionCatalog.AccountCreate, new ObjectRef(ObjectTypes.Account, ExistingEntitlement.Id), Container, new Dictionary<string, string?> { [AccountAttribute(connector)] = "Clash" });
        (await connector.ExecuteAsync(clash, Ct)).Code.ShouldBe(ResultCodes.AlreadyExists);
        (await connector.ReadAsync(ExistingEntitlement, Ct)).ShouldNotBeNull("the entitlement is untouched");
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
            (await connector.ExecuteAsync(Op(ActionCatalog.EntitlementAdd, account, AbsentEntitlement), Ct))
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
