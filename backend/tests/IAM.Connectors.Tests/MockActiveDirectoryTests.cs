using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using Shouldly;

namespace IAM.Connectors.Tests;

/// <summary>The contract suite against the mock Active Directory connector (DL-081): it must pass all of it.</summary>
public sealed class MockActiveDirectoryContractTests : ConnectorContractTests
{
    private const string Base = MockActiveDirectoryConnector.DefaultBaseDn;

    protected override ObjectRef Container => new(ObjectTypes.Container, $"OU=Staff,{Base}");

    protected override ObjectRef ExistingEntitlement => new(ObjectTypes.Entitlement, $"CN=Group A,OU=Groups,{Base}");

    protected override ObjectRef AbsentEntitlement => new(ObjectTypes.Entitlement, $"CN=No Such Group,OU=Groups,{Base}");

    protected override ObjectRef? MalformedAccount => new(ObjectTypes.Account, $"CN=user,OU=Staff,DC=elsewhere,DC=test");

    protected override bool OneNamespace => true;

    protected override IConnector Create(ConnectorSettings settings) => MockActiveDirectoryTests.Directory(settings);

    protected override ObjectRef NewAccount() => new(ObjectTypes.Account, $"CN=user-{Guid.CreateVersion7():N},OU=Staff,{Base}");
}

/// <summary>What the mock Active Directory connector does beyond the contract (DL-081).</summary>
public sealed class MockActiveDirectoryTests
{
    private const string Base = MockActiveDirectoryConnector.DefaultBaseDn;
    private static readonly ObjectRef Staff = new(ObjectTypes.Container, $"OU=Staff,{Base}");
    private static readonly ObjectRef Groups = new(ObjectTypes.Container, $"OU=Groups,{Base}");
    private static readonly ObjectRef GroupA = new(ObjectTypes.Entitlement, $"CN=Group A,OU=Groups,{Base}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A mock directory with OUs Staff and Groups and the group Group A.</summary>
    public static MockActiveDirectoryConnector Directory(ConnectorSettings settings)
    {
        var directory = new MockActiveDirectoryConnector(settings, new InMemoryOperationLedger());
        directory.AddContainer("Staff");
        directory.AddGroup("Group A", directory.AddContainer("Groups"));
        return directory;
    }

    private static ConnectorOperation Op(string action, ObjectRef target, ObjectRef? related = null, IReadOnlyDictionary<string, string?>? attributes = null, SecretReference? secret = null) =>
        new(Guid.CreateVersion7(), action, target, related, attributes ?? new Dictionary<string, string?>(), secret);

    private static async Task<ObjectRef> UserAsync(MockActiveDirectoryConnector directory, string name)
    {
        var user = new ObjectRef(ObjectTypes.Account, $"CN={name},OU=Staff,{Base}");
        (await directory.ExecuteAsync(Op(ActionCatalog.AccountCreate, user, Staff, new Dictionary<string, string?> { ["displayName"] = name }), Ct)).Status.ShouldBe(OperationStatus.Applied);
        return user;
    }

    [Fact]
    public async Task ItSaysItIsAMockEverywhere()
    {
        var directory = Directory(ConnectorSettings.Default);
        directory.Capabilities.ConnectorType.ShouldBe("activedirectory.mock");
        directory.Capabilities.ProductionReady.ShouldBeFalse();
        directory.Capabilities.Actions.ShouldBe(ActionCatalog.All.Select(a => a.Code), ignoreOrder: true);
        var health = await directory.CheckHealthAsync(Ct);
        health.ProductionReady.ShouldBeFalse();
        health.Code.ShouldBe("Mock");
    }

    [Theory]
    [InlineData("CN=Ann,OU=Staff,DC=example,DC=test", true)]
    [InlineData("CN=Example\\, Ann,OU=Staff,DC=example,DC=test", true)]
    [InlineData("CN=Ann,OU=Staff,DC=other,DC=test", false)]
    [InlineData("CN=Ann,DC=example,DC=test", false)]
    [InlineData("OU=Staff,DC=example,DC=test", false)]
    [InlineData("CN=Ann+UID=1,OU=Staff,DC=example,DC=test", false)]
    [InlineData("CN=Ann;x,OU=Staff,DC=example,DC=test", false)]
    [InlineData("CN=Ann\\41,OU=Staff,DC=example,DC=test", false)]
    [InlineData("CN= Ann,OU=Staff,DC=example,DC=test", false)]
    [InlineData("cn=Ann,OU=Staff,DC=example,DC=test", false)]
    [InlineData("UID=Ann,OU=Staff,DC=example,DC=test", false)]
    [InlineData("CN=Ann,OU=Staff,L=Leeds,DC=example,DC=test", false)]
    [InlineData("CN=Ann,OU=Staff,DC=example,DC=test\\", false)]
    public async Task OnlyNamesInsideTheDirectoryInTheRightShapeAreAccepted(string dn, bool accepted)
    {
        var directory = Directory(ConnectorSettings.Default);
        var create = Op(ActionCatalog.AccountCreate, new ObjectRef(ObjectTypes.Account, dn), Staff, new Dictionary<string, string?> { ["displayName"] = "Ann" });
        var result = await directory.ExecuteAsync(create, Ct);
        if (accepted)
        {
            result.Status.ShouldBe(OperationStatus.Applied);
        }
        else
        {
            result.ShouldBe(new OperationResult(create.OperationId, OperationStatus.Rejected, ResultCodes.InvalidOperation));
        }
    }

    [Fact]
    public async Task AUserOutsideAnOrganisationalUnitIsNotANameAtAll()
    {
        // Not just "not found": a CN straight under the domain isn't a user or group name the mock accepts.
        var directory = Directory(ConnectorSettings.Default);
        var add = Op(ActionCatalog.MemberAdd, GroupA, new ObjectRef(ObjectTypes.Account, $"CN=Ann,{Base}"));
        (await directory.ExecuteAsync(add, Ct)).ShouldBe(new OperationResult(add.OperationId, OperationStatus.Rejected, ResultCodes.InvalidOperation));
    }

    [Fact]
    public void DistinguishedNamesParseWithEscapes()
    {
        DistinguishedName.Parse("CN=Example\\, Ann,OU=Staff,DC=example,DC=test")!.Select(p => p.Type).ShouldBe(["CN", "OU", "DC", "DC"]);
        DistinguishedName.Parent("CN=Example\\, Ann,OU=Staff,DC=example,DC=test").ShouldBe("OU=Staff,DC=example,DC=test");
        DistinguishedName.Parse(new string('a', 513)).ShouldBeNull();
        Should.Throw<ArgumentException>(() => new MockActiveDirectoryConnector(ConnectorSettings.Default, new InMemoryOperationLedger(), "OU=Staff,DC=example"));
    }

    [Fact]
    public async Task AnAccountIsCreatedDisabledInTheContainerNamed()
    {
        var directory = Directory(ConnectorSettings.Default);
        var user = await UserAsync(directory, "Ann");
        var snapshot = (await directory.ReadAsync(user, Ct)).ShouldNotBeNull();
        snapshot.Enabled.ShouldBe(false, "a new AD account starts disabled");
        snapshot.Locked.ShouldBe(false);
        snapshot.Attributes["displayName"].ShouldBe("Ann");

        var elsewhere = Op(ActionCatalog.AccountCreate, new ObjectRef(ObjectTypes.Account, $"CN=Bob,OU=Staff,{Base}"), Groups, new Dictionary<string, string?> { ["displayName"] = "Bob" });
        (await directory.ExecuteAsync(elsewhere, Ct)).ShouldBe(new OperationResult(elsewhere.OperationId, OperationStatus.Rejected, ResultCodes.InvalidOperation));
    }

    [Fact]
    public async Task MembershipIsTheSameFromEitherSide()
    {
        var directory = Directory(ConnectorSettings.Default);
        var ann = await UserAsync(directory, "Ann");
        var bob = await UserAsync(directory, "Bob");

        (await directory.ExecuteAsync(Op(ActionCatalog.EntitlementAdd, ann, GroupA), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ReadAsync(GroupA, Ct)).ShouldNotBeNull().Links.ShouldBe([ann]);
        (await directory.ExecuteAsync(Op(ActionCatalog.MemberAdd, GroupA, ann), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);

        (await directory.ExecuteAsync(Op(ActionCatalog.MemberAdd, GroupA, bob), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ReadAsync(bob, Ct)).ShouldNotBeNull().Links.ShouldBe([GroupA]);

        (await directory.ExecuteAsync(Op(ActionCatalog.MemberRemove, GroupA, ann), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ReadAsync(ann, Ct)).ShouldNotBeNull().Links.ShouldBeEmpty();
        (await directory.ExecuteAsync(Op(ActionCatalog.EntitlementRemove, ann, GroupA), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);
    }

    [Fact]
    public async Task DeletingLeavesNoDanglingMembership()
    {
        var directory = Directory(ConnectorSettings.Allowing(ActionCatalog.AccountDelete, ActionCatalog.EntitlementDelete));
        var ann = await UserAsync(directory, "Ann");
        var bob = await UserAsync(directory, "Bob");
        var temp = new ObjectRef(ObjectTypes.Entitlement, $"CN=Temp,OU=Groups,{Base}");
        (await directory.ExecuteAsync(Op(ActionCatalog.EntitlementCreate, temp, Groups, new Dictionary<string, string?> { ["description"] = "Temporary" }), Ct)).Status.ShouldBe(OperationStatus.Applied);
        foreach (var user in new[] { ann, bob })
        {
            await directory.ExecuteAsync(Op(ActionCatalog.EntitlementAdd, user, temp), Ct);
            await directory.ExecuteAsync(Op(ActionCatalog.EntitlementAdd, user, GroupA), Ct);
        }

        (await directory.ExecuteAsync(Op(ActionCatalog.EntitlementDelete, temp), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ReadAsync(ann, Ct)).ShouldNotBeNull().Links.ShouldBe([GroupA]);
        (await directory.ExecuteAsync(Op(ActionCatalog.AccountDelete, bob), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ReadAsync(GroupA, Ct)).ShouldNotBeNull().Links.ShouldBe([ann]);
    }

    [Fact]
    public async Task AnOwnerIsWrittenBack()
    {
        var directory = Directory(ConnectorSettings.Default);
        var service = await UserAsync(directory, "svc-reports");
        var ann = await UserAsync(directory, "Ann");
        (await directory.ExecuteAsync(Op(ActionCatalog.AccountWriteOwner, service, ann), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ReadAsync(service, Ct)).ShouldNotBeNull().Owner.ShouldBe(ann);
        (await directory.ExecuteAsync(Op(ActionCatalog.AccountWriteOwner, service, ann), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);

        // Deleting the owner leaves no ownership pointing at a name someone else could later be created under.
        var deleting = Directory(ConnectorSettings.Allowing(ActionCatalog.AccountDelete));
        var svc = await UserAsync(deleting, "svc-reports");
        var bob = await UserAsync(deleting, "Bob");
        await deleting.ExecuteAsync(Op(ActionCatalog.AccountWriteOwner, svc, bob), Ct);
        (await deleting.ExecuteAsync(Op(ActionCatalog.AccountDelete, bob), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await deleting.ReadAsync(svc, Ct)).ShouldNotBeNull().Owner.ShouldBeNull();
    }

    [Fact]
    public async Task CredentialActionsChangeStateButNeverHoldASecret()
    {
        var directory = Directory(ConnectorSettings.Allowing(ActionCatalog.PasswordChange, ActionCatalog.PasswordReset) with { SecretVault = "customer-vault", SecretPathPrefix = "people/" });
        var ann = await UserAsync(directory, "Ann");

        (await directory.ExecuteAsync(Op(ActionCatalog.PasswordReset, ann), Ct)).Status.ShouldBe(OperationStatus.Applied);
        directory.MustChangePassword(ann).ShouldBeTrue();
        var change = Op(ActionCatalog.PasswordChange, ann, secret: new SecretReference("customer-vault", "people/ann"));
        (await directory.ExecuteAsync(change, Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ExecuteAsync(change, Ct)).Status.ShouldBe(OperationStatus.Applied, "the repeat is answered from the ledger");
        directory.PasswordChanges(ann).ShouldBe(1, "a repeated operation id never changes a password twice");
        directory.MustChangePassword(ann).ShouldBeFalse();

        (await directory.ExecuteAsync(Op(ActionCatalog.AccountUnlock, ann), Ct)).Status.ShouldBe(OperationStatus.AlreadyInState);
        directory.Lock(ann);
        (await directory.ExecuteAsync(Op(ActionCatalog.AccountUnlock, ann), Ct)).Status.ShouldBe(OperationStatus.Applied);
        (await directory.ReadAsync(ann, Ct)).ShouldNotBeNull().Locked.ShouldBe(false);
    }

    [Fact]
    public async Task EveryObjectTypeCanBeListedAndBadContinuationsAreRefused()
    {
        var directory = Directory(ConnectorSettings.Default);
        (await directory.ListAsync(ObjectTypes.Container, null, 10, Ct)).Items.Select(i => i.Ref).ShouldBe([Groups, Staff]);
        (await directory.ListAsync(ObjectTypes.Entitlement, null, 10, Ct)).Items.Select(i => i.Ref).ShouldBe([GroupA]);
        await Should.ThrowAsync<ArgumentException>(() => directory.ListAsync(ObjectTypes.Account, "not base64!", 10, Ct));
        await Should.ThrowAsync<ArgumentException>(() => directory.ListAsync(ObjectTypes.Account, Convert.ToBase64String("CN=x,DC=other"u8.ToArray()), 10, Ct));
    }
}
