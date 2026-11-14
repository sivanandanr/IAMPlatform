using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.Modules.Catalog.Domain.Applications;
using Shouldly;
using CatalogApplication = IAM.Modules.Catalog.Domain.Applications.Application;

namespace IAM.Persistence.Tests.Catalog;

/// <summary>Application catalog rules without a database (DL-066, AR-2.1 to AR-2.4, IAM-FR-010).</summary>
public sealed class ApplicationCatalogDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    internal static ApplicationSettings Settings(string owner = "owner-1", RoleHolderKind? kind = RoleHolderKind.Group, string? holder = "grp-it-admins", RoleRequirement requirement = RoleRequirement.Required) =>
        new(owner, [new ApplicationRoleSetting(BuiltInRoleTypes.ItAdmin, requirement, kind, holder)]);

    [Theory]
    [InlineData("sap_finance")]
    [InlineData("ad")]
    [InlineData("a1_b2")]
    public void KeysAreLowerCaseCodes(string key) => Should.NotThrow(() => CatalogRules.ValidateKey(key, "Key"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("SAP")]
    [InlineData("1app")]
    [InlineData("_app")]
    [InlineData("app-name")]
    [InlineData("app name")]
    [InlineData("app\n")]
    [InlineData("appı")]
    public void OtherKeysAreRejected(string? key) =>
        Should.Throw<ApplicationRejectedException>(() => CatalogRules.ValidateKey(key, "Key")).Field.ShouldBe("Key");

    [Fact]
    public void TheOwnerIsOnePerson()
    {
        foreach (var owner in new[] { "", " owner", "owner ", "own‮er", new string('a', 201) })
        {
            Should.Throw<ApplicationRejectedException>(() => CatalogRules.ValidateSettings(Settings(owner: owner))).Field.ShouldBe("Owner");
        }
    }

    [Fact]
    public void EachRoleTypeAppearsOnceAndAHolderHasAKindAndAnId()
    {
        var twice = new ApplicationSettings("owner-1",
        [
            new ApplicationRoleSetting(BuiltInRoleTypes.ItAdmin, RoleRequirement.Optional, null, null),
            new ApplicationRoleSetting(BuiltInRoleTypes.ItAdmin, RoleRequirement.Required, RoleHolderKind.Person, "p-1"),
        ]);
        Should.Throw<ApplicationRejectedException>(() => CatalogRules.ValidateSettings(twice)).Field.ShouldBe("Roles");
        Should.Throw<ApplicationRejectedException>(() => CatalogRules.ValidateSettings(Settings(kind: RoleHolderKind.Person, holder: null))).Field.ShouldBe("Holder");
        Should.Throw<ApplicationRejectedException>(() => CatalogRules.ValidateSettings(Settings(kind: null, holder: "p-1"))).Field.ShouldBe("Holder");
        Should.Throw<ApplicationRejectedException>(() => CatalogRules.ValidateSettings(Settings(kind: (RoleHolderKind)9))).Field.ShouldBe("Holder");
        Should.Throw<ApplicationRejectedException>(() => CatalogRules.ValidateSettings(Settings(requirement: (RoleRequirement)9))).Field.ShouldBe("Requirement");
        Should.Throw<ApplicationRejectedException>(() => CatalogRules.ValidateSettings(new ApplicationSettings("owner-1",
            [.. Enumerable.Range(0, CatalogRules.MaxRoles + 1).Select(_ => new ApplicationRoleSetting(Guid.NewGuid(), RoleRequirement.Optional, null, null))]))).Field.ShouldBe("Roles");
        Should.NotThrow(() => CatalogRules.ValidateSettings(new ApplicationSettings("owner-1", [])));
    }

    [Fact]
    public void TheHashCoversOwnerRolesAndHoldersButNotRoleOrder()
    {
        var a = new ApplicationSettings("owner-1",
        [
            new ApplicationRoleSetting(BuiltInRoleTypes.ItAdmin, RoleRequirement.Required, RoleHolderKind.Group, "g-1"),
            new ApplicationRoleSetting(BuiltInRoleTypes.UserAdmin, RoleRequirement.Optional, null, null),
        ]);
        var reordered = a with { Roles = [.. a.Roles.Reverse()] };
        var hash = ApplicationSettingsVersion.ComputeContentHash("app", 1, a);
        hash.ShouldMatch("^[0-9a-f]{64}$");
        ApplicationSettingsVersion.ComputeContentHash("app", 1, reordered).ShouldBe(hash);
        ApplicationSettingsVersion.ComputeContentHash("app", 2, a).ShouldNotBe(hash);
        ApplicationSettingsVersion.ComputeContentHash("app", 1, a with { Owner = "owner-2" }).ShouldNotBe(hash);
        ApplicationSettingsVersion.ComputeContentHash("app", 1, a with { Roles = [a.Roles[0] with { HolderKind = RoleHolderKind.Person }, a.Roles[1]] }).ShouldNotBe(hash);
        ApplicationSettingsVersion.ComputeContentHash("app", 1, a with { Roles = [a.Roles[0] with { HolderId = "G-1" }, a.Roles[1]] }).ShouldNotBe(hash);
        ApplicationSettingsVersion.ComputeContentHash("app", 1, a with { Roles = [a.Roles[0] with { Requirement = RoleRequirement.Optional }, a.Roles[1]] }).ShouldNotBe(hash);
    }

    [Fact]
    public void ARequiredRoleThatIsEmptyBlocksTheProposalAndActivation()
    {
        var application = CatalogApplication.Register("crm", "CRM", Settings(kind: null, holder: null));
        application.CurrentDraft.ShouldNotBeNull().HasEmptyRequiredRole.ShouldBeTrue();
        Should.Throw<ApplicationRefusedException>(() => ApplicationSettingsRequest.Propose(Guid.NewGuid(), application, "maker", "Ready", Now))
            .Refusal.ShouldBe(ApplicationRefusal.RequiredRoleEmpty);

        // An optional empty role is fine (AR-2.6 decides at request time, stage 4).
        var optional = CatalogApplication.Register("crm2", "CRM", Settings(kind: null, holder: null, requirement: RoleRequirement.Optional));
        Should.NotThrow(() => ApplicationSettingsRequest.Propose(Guid.NewGuid(), optional, "maker", "Ready", Now));
    }

    [Fact]
    public void ASecondPersonWhoIsNotTheOwnerActivatesExactlyWhatWasProposed()
    {
        var application = CatalogApplication.Register("erp", "ERP", Settings());
        var request = ApplicationSettingsRequest.Propose(Guid.NewGuid(), application, "maker", "Ready", Now);
        request.ProposedOwner.ShouldBe("owner-1");

        Should.Throw<ApplicationRefusedException>(() => request.Approve("maker", null, application, Now)).Refusal.ShouldBe(ApplicationRefusal.MakerCannotDecide);
        Should.Throw<ApplicationRefusedException>(() => request.Approve("owner-1", null, application, Now)).Refusal.ShouldBe(ApplicationRefusal.OwnerCannotDecide);
        Should.Throw<ApplicationRefusedException>(() => request.Reject("owner-1", "No", Now)).Refusal.ShouldBe(ApplicationRefusal.OwnerCannotDecide);
        application.CurrentActive.ShouldBeNull();

        request.Approve("checker", null, application, Now);
        request.State.ShouldBe(SecondPersonState.Approved);
        var active = application.CurrentActive.ShouldNotBeNull();
        active.VersionNumber.ShouldBe(1);
        active.ActivatedByRequestId.ShouldBe(request.Id);
        application.CurrentDraft.ShouldBeNull();
        Should.Throw<ApplicationRefusedException>(() => request.Approve("checker-2", null, application, Now)).Refusal.ShouldBe(ApplicationRefusal.NotProposed);
    }

    [Fact]
    public void AnEditedDraftCannotBeActivatedByTheEarlierRequest()
    {
        var application = CatalogApplication.Register("hr", "HR", Settings());
        var request = ApplicationSettingsRequest.Propose(Guid.NewGuid(), application, "maker", "Ready", Now);
        application.UpdateDraft(Settings(owner: "owner-2")).ShouldBeTrue();
        Should.Throw<ApplicationRefusedException>(() => request.Approve("checker", null, application, Now)).Refusal.ShouldBe(ApplicationRefusal.DraftChanged);
        application.CurrentActive.ShouldBeNull();
    }

    [Fact]
    public void ANewVersionSupersedesTheActiveOne()
    {
        var application = CatalogApplication.Register("mail", "Mail", Settings());
        Should.Throw<ApplicationRefusedException>(() => application.StartNewDraft(Settings())).Refusal.ShouldBe(ApplicationRefusal.DraftExists);
        ApplicationSettingsRequest.Propose(Guid.NewGuid(), application, "maker", "Ready", Now).Approve("checker", null, application, Now);

        var next = application.StartNewDraft(Settings(owner: "owner-2"));
        next.VersionNumber.ShouldBe(2);
        Should.Throw<ApplicationRefusedException>(() => application.StartNewDraft(Settings())).Refusal.ShouldBe(ApplicationRefusal.DraftExists);
        var request = ApplicationSettingsRequest.Propose(Guid.NewGuid(), application, "maker", "New owner", Now);
        Should.Throw<ApplicationRefusedException>(() => request.Approve("owner-2", null, application, Now)).Refusal.ShouldBe(ApplicationRefusal.OwnerCannotDecide);
        request.Approve("owner-1", null, application, Now);

        application.CurrentActive.ShouldNotBeNull().Owner.ShouldBe("owner-2");
        application.Versions.Single(v => v.VersionNumber == 1).State.ShouldBe(SettingsVersionState.Superseded);
    }

    [Fact]
    public void ANewDraftCannotStartBeforeTheFirstIsActivated()
    {
        var application = CatalogApplication.Register("wiki", "Wiki", Settings());
        var request = ApplicationSettingsRequest.Propose(Guid.NewGuid(), application, "maker", "Ready", Now);
        request.Withdraw("maker", null, Now);
        Should.Throw<ApplicationRefusedException>(() => application.StartNewDraft(Settings())).Refusal.ShouldBe(ApplicationRefusal.DraftExists);
    }

    [Fact]
    public void ARenameIsNotAccess()
    {
        var application = CatalogApplication.Register("wiki2", "Wiki", Settings());
        application.Rename("Wiki").ShouldBeFalse();
        application.Rename("Team wiki").ShouldBeTrue();
        Should.Throw<ApplicationRejectedException>(() => application.Rename(" ")).Field.ShouldBe("DisplayName");
        application.CurrentDraft.ShouldNotBeNull().VersionNumber.ShouldBe(1);
    }

    [Fact]
    public void ADamagedRequestIsRefused()
    {
        var hash = new string('a', 64);
        Should.Throw<ApplicationRefusedException>(() => ApplicationSettingsRequest.Restore(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, hash, "owner-1", "maker", "r", Now, SecondPersonState.Approved, "owner-1", null, Now))
            .Refusal.ShouldBe(ApplicationRefusal.InvalidRecord);
        Should.Throw<ApplicationRefusedException>(() => ApplicationSettingsRequest.Restore(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "ABC", "owner-1", "maker", "r", Now, SecondPersonState.Proposed, null, null, null))
            .Refusal.ShouldBe(ApplicationRefusal.InvalidRecord);
        Should.Throw<ApplicationRefusedException>(() => ApplicationSettingsRequest.Restore(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, hash, "owner-1", "maker", "r", Now, SecondPersonState.Approved, "maker", null, Now))
            .Refusal.ShouldBe(ApplicationRefusal.InvalidRecord);
        Should.NotThrow(() => ApplicationSettingsRequest.Restore(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, hash, "owner-1", "maker", "r", Now, SecondPersonState.Approved, "checker", null, Now));
    }

    [Fact]
    public void ARoleTypeIsUsableOnlyOnceASecondPersonApprovesIt()
    {
        var type = ApplicationRoleType.Propose("security_contact", "Security contact", "maker", "Needed by audit", Now);
        type.State.ShouldBe(RoleTypeState.Proposed);
        type.IsUsable.ShouldBeFalse();
        type.HoldsCode.ShouldBeTrue();
        Should.Throw<ApplicationRefusedException>(() => type.Approve("maker", null, Now)).Refusal.ShouldBe(ApplicationRefusal.MakerCannotDecide);
        Should.Throw<ApplicationRefusedException>(() => type.Reject("checker", null, Now)).Refusal.ShouldBe(ApplicationRefusal.InvalidReason);
        Should.Throw<ApplicationRefusedException>(() => type.Withdraw("checker", null, Now)).Refusal.ShouldBe(ApplicationRefusal.NotMaker);

        type.Approve("checker", "Agreed", Now);
        type.State.ShouldBe(RoleTypeState.Approved);
        type.IsUsable.ShouldBeTrue();
        type.Checker.ShouldBe("checker");
        Should.Throw<ApplicationRefusedException>(() => type.Reject("checker-2", "Late", Now)).Refusal.ShouldBe(ApplicationRefusal.NotProposed);

        var withdrawn = ApplicationRoleType.Propose("vendor_contact", "Vendor contact", "maker", "Maybe", Now);
        withdrawn.Withdraw("maker", null, Now);
        withdrawn.HoldsCode.ShouldBeFalse();
        withdrawn.IsUsable.ShouldBeFalse();

        Should.Throw<ApplicationRejectedException>(() => ApplicationRoleType.Propose("Bad Code", "x", "maker", "r", Now)).Field.ShouldBe("Code");
        Should.Throw<ApplicationRefusedException>(() => ApplicationRoleType.Propose("ok_code", "x", "maker", " ", Now)).Refusal.ShouldBe(ApplicationRefusal.InvalidReason);
    }

    [Fact]
    public void BuiltInRoleTypesAreTheThreeStandardOnes()
    {
        BuiltInRoleTypes.All.Select(t => t.Code).ShouldBe(["user_admin", "it_admin", "technical_custodian"]);
        BuiltInRoleTypes.All.Select(t => t.Id).Distinct().Count().ShouldBe(3);
    }
}
