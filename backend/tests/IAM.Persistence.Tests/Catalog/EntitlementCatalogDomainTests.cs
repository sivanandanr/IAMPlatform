using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.Modules.Catalog.Application.Entitlements;
using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Domain.Entitlements;
using Shouldly;

namespace IAM.Persistence.Tests.Catalog;

/// <summary>Entitlement catalog rules without a database (DL-084h, DL-087, spec v3.2 §2.5, §3.1, §6.3, §6.4).</summary>
public sealed class EntitlementCatalogDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid App = Guid.CreateVersion7();

    internal static EntitlementSettings Settings(
        string owner = "owner-1",
        RoleHolderKind ownerKind = RoleHolderKind.Person,
        bool privileged = false,
        AccountClass[]? classes = null,
        AccessFor? accessFor = null,
        string technicalName = "CN=Finance Readers,OU=Groups,DC=corp,DC=test") =>
        new(
            EntitlementType.SecurityGroup,
            technicalName,
            "Read access to the finance share",
            ownerKind,
            owner,
            "grp_finance_escalation",
            privileged,
            EntitlementRisk.Medium,
            classes ?? (privileged ? [AccountClass.Secondary] : [AccountClass.Primary, AccountClass.Service]),
            accessFor ?? (privileged ? AccessFor.Fixed(14) : AccessFor.NoExpiry));

    private static Entitlement Registered(EntitlementSettings? settings = null, string editor = "author") =>
        Entitlement.Register(App, "finance_readers", "Finance readers", settings ?? Settings(), editor);

    [Fact]
    public void PrivilegedAccessNeverGoesOnAPrimaryAccountAndAlwaysEnds()
    {
        Should.Throw<ApplicationRefusedException>(() => EntitlementRules.Validate(Settings(privileged: true, classes: [AccountClass.Primary, AccountClass.Secondary])))
            .Refusal.ShouldBe(ApplicationRefusal.PrivilegedOnPrimary);
        Should.Throw<ApplicationRefusedException>(() => EntitlementRules.Validate(Settings(privileged: true, accessFor: AccessFor.NoExpiry)))
            .Refusal.ShouldBe(ApplicationRefusal.PrivilegedNeedsEndDate);
        Should.Throw<ApplicationRefusedException>(() => EntitlementRules.Validate(Settings(classes: [AccountClass.Secondary])))
            .Refusal.ShouldBe(ApplicationRefusal.SecondaryOnlyForPrivileged);
        Should.NotThrow(() => EntitlementRules.Validate(Settings(privileged: true, classes: [AccountClass.Secondary, AccountClass.Service], accessFor: AccessFor.RequesterChooses(7, 30))));
    }

    [Fact]
    public void AccessForIsOneOfTheThreeShapes()
    {
        foreach (var bad in new[]
        {
            new AccessFor(AccessForMode.NoExpiry, 5, null, null),
            AccessFor.Fixed(0),
            AccessFor.Fixed(EntitlementRules.MaxDays + 1),
            new AccessFor(AccessForMode.FixedDays, 5, 5, null),
            AccessFor.RequesterChooses(31, 30),
            AccessFor.RequesterChooses(0, 30),
            new AccessFor(AccessForMode.RequesterChooses, 3, 7, 30),
            new AccessFor((AccessForMode)99, null, null, null),
        })
        {
            Should.Throw<ApplicationRejectedException>(() => EntitlementRules.Validate(Settings(accessFor: bad)), bad.ToString()).Field.ShouldBe("AccessFor");
        }
    }

    [Fact]
    public void OwnersEscalationAndAccountTypesAreChecked()
    {
        Should.Throw<ApplicationRejectedException>(() => EntitlementRules.Validate(Settings(owner: " owner"))).Field.ShouldBe("Owner");
        Should.Throw<ApplicationRejectedException>(() => EntitlementRules.Validate(Settings(owner: "Not A Key", ownerKind: RoleHolderKind.Group))).Field.ShouldBe("Owner");
        Should.Throw<ApplicationRejectedException>(() => EntitlementRules.Validate(Settings() with { EscalationWorkgroup = "" })).Field.ShouldBe("EscalationWorkgroup");
        Should.Throw<ApplicationRejectedException>(() => EntitlementRules.Validate(Settings(classes: []))).Field.ShouldBe("AllowedAccountClasses");
        Should.Throw<ApplicationRejectedException>(() => EntitlementRules.Validate(Settings(classes: [AccountClass.Service, AccountClass.Service]))).Field.ShouldBe("AllowedAccountClasses");
        Should.Throw<ApplicationRejectedException>(() => EntitlementRules.Validate(Settings(technicalName: " padded"))).Field.ShouldBe("TechnicalName");
        Should.Throw<ApplicationRejectedException>(() => EntitlementRules.Validate(Settings(technicalName: "bad\nname"))).Field.ShouldBe("TechnicalName");
        Should.NotThrow(() => EntitlementRules.Validate(Settings(owner: "grp_finance_owners", ownerKind: RoleHolderKind.Group)));
    }

    [Fact]
    public void TheApiTakesOnlyExactNames()
    {
        var text = new EntitlementSettingsText("SecurityGroup", "CN=x", "d", "Person", "owner-1", "grp_esc", false, "Low", ["Primary"], "NoExpiry", null, null, null);
        Should.NotThrow(() => EntitlementApi.ToSettings(text));
        Should.Throw<ApplicationRejectedException>(() => EntitlementApi.ToSettings(text with { Type = "securitygroup" })).Field.ShouldBe("Type");
        Should.Throw<ApplicationRejectedException>(() => EntitlementApi.ToSettings(text with { Risk = "3" })).Field.ShouldBe("Risk");
        Should.Throw<ApplicationRejectedException>(() => EntitlementApi.ToSettings(text with { AllowedAccountClasses = ["User"] })).Field.ShouldBe("AllowedAccountClasses");
        Should.Throw<ApplicationRejectedException>(() => EntitlementApi.ToSettings(text with { AllowedAccountClasses = ["NHI"] })).Field.ShouldBe("AllowedAccountClasses");
        Should.Throw<ApplicationRejectedException>(() => EntitlementApi.ToSettings(text with { Privileged = null })).Field.ShouldBe("Privileged");
    }

    [Fact]
    public void AccountClassesAreStoredInOneSpelling()
    {
        EntitlementRules.ToStored([AccountClass.Shared, AccountClass.Primary]).ShouldBe("Primary,Shared");
        EntitlementRules.FromStored("Primary,Shared").ShouldBe([AccountClass.Primary, AccountClass.Shared]);
        foreach (var bad in new[] { "Shared,Primary", "Primary,,Shared", "primary", "Primary,Primary", "", "NHI" })
        {
            Should.Throw<ApplicationRefusedException>(() => EntitlementRules.FromStored(bad), bad).Refusal.ShouldBe(ApplicationRefusal.InvalidRecord);
        }
    }

    [Fact]
    public void TheHashCoversEverythingThatDecidesAccess()
    {
        var baseline = EntitlementRules.ComputeContentHash("k", 1, Settings());
        EntitlementRules.ComputeContentHash("k", 1, Settings()).ShouldBe(baseline);
        foreach (var other in new[]
        {
            Settings(owner: "owner-2"),
            Settings(owner: "owner-1", ownerKind: RoleHolderKind.Group),
            Settings(classes: [AccountClass.Primary]),
            Settings(accessFor: AccessFor.Fixed(30)),
            Settings(technicalName: "CN=Other"),
            Settings() with { Risk = EntitlementRisk.High },
            Settings() with { EscalationWorkgroup = "grp_other" },
            Settings() with { Type = EntitlementType.SharedMailbox },
            Settings() with { Description = "Other" },
        })
        {
            EntitlementRules.ComputeContentHash("k", 1, other).ShouldNotBe(baseline);
        }

        EntitlementRules.ComputeContentHash("k", 2, Settings()).ShouldNotBe(baseline);
        EntitlementRules.ComputeContentHash("j", 1, Settings()).ShouldNotBe(baseline);
    }

    [Fact]
    public void ANewOwnerAcceptsTheExactDraftAndAnEditUndoesIt()
    {
        var entitlement = Registered();
        entitlement.DraftNeedsAcceptance.ShouldBeTrue("the first version always needs the owner");
        var hash = entitlement.CurrentDraft!.ContentHash;

        Should.Throw<ApplicationRefusedException>(() => EntitlementChangeRequest.Propose(Guid.CreateVersion7(), entitlement, "author", "Ready", Now))
            .Refusal.ShouldBe(ApplicationRefusal.OwnerNotAccepted);
        Should.Throw<ApplicationRefusedException>(() => entitlement.AcceptOwnership("someone-else", isOwner: true, hash, Now))
            .Refusal.ShouldBe(ApplicationRefusal.NotOwner, "for a person owner only that person accepts, whatever the caller says");
        Should.Throw<ApplicationRefusedException>(() => entitlement.AcceptOwnership("owner-1", false, new string('0', 64), Now))
            .Refusal.ShouldBe(ApplicationRefusal.DraftChanged);

        entitlement.AcceptOwnership("owner-1", false, hash, Now).ShouldBeTrue();
        entitlement.AcceptOwnership("owner-1", false, hash, Now).ShouldBeFalse("already accepted");

        entitlement.UpdateDraft(Settings(classes: [AccountClass.Primary]), "author").ShouldBeTrue();
        entitlement.CurrentDraft!.IsAccepted.ShouldBeFalse("what was accepted was the old content");
        Should.Throw<ApplicationRefusedException>(() => EntitlementChangeRequest.Propose(Guid.CreateVersion7(), entitlement, "author", "Ready", Now))
            .Refusal.ShouldBe(ApplicationRefusal.OwnerNotAccepted);
    }

    [Fact]
    public void AWorkgroupOwnerIsAcceptedByAMember()
    {
        var entitlement = Registered(Settings(owner: "grp_finance_owners", ownerKind: RoleHolderKind.Group));
        var hash = entitlement.CurrentDraft!.ContentHash;
        Should.Throw<ApplicationRefusedException>(() => entitlement.AcceptOwnership("grp_finance_owners", false, hash, Now)).Refusal.ShouldBe(ApplicationRefusal.NotOwner);
        entitlement.AcceptOwnership("member-1", true, hash, Now).ShouldBeTrue();
        entitlement.CurrentDraft.OwnerAcceptedBy.ShouldBe("member-1");
    }

    [Fact]
    public void NobodyWhoWroteOwnsOrAcceptedTheDraftCanDecideIt()
    {
        var entitlement = Registered(editor: "author");
        entitlement.AcceptOwnership("owner-1", false, entitlement.CurrentDraft!.ContentHash, Now);
        var request = EntitlementChangeRequest.Propose(Guid.CreateVersion7(), entitlement, "maker", "Ready", Now);
        request.AcceptedBy.ShouldBe("owner-1");

        Should.Throw<ApplicationRefusedException>(() => request.Approve("maker", null, entitlement, false, Now)).Refusal.ShouldBe(ApplicationRefusal.MakerCannotDecide);
        Should.Throw<ApplicationRefusedException>(() => request.Approve("owner-1", null, entitlement, false, Now)).Refusal.ShouldBe(ApplicationRefusal.OwnerCannotDecide);
        Should.Throw<ApplicationRefusedException>(() => request.Approve("checker", null, entitlement, memberOfOwner: true, Now)).Refusal.ShouldBe(ApplicationRefusal.OwnerCannotDecide);
        Should.Throw<ApplicationRefusedException>(() => request.Approve("author", null, entitlement, false, Now)).Refusal.ShouldBe(ApplicationRefusal.MakerCannotDecide, "the content's author");

        request.Approve("checker", "Fine", entitlement, false, Now);
        entitlement.CurrentActive.ShouldNotBeNull().ActivatedByRequestId.ShouldBe(request.Id);
        entitlement.CurrentDraft.ShouldBeNull();
    }

    [Fact]
    public void TheAcceptorCanNotDecideAWorkgroupOwnedDraft()
    {
        var entitlement = Registered(Settings(owner: "grp_finance_owners", ownerKind: RoleHolderKind.Group));
        entitlement.AcceptOwnership("member-1", true, entitlement.CurrentDraft!.ContentHash, Now);
        var request = EntitlementChangeRequest.Propose(Guid.CreateVersion7(), entitlement, "maker", "Ready", Now);

        // Even if they left the workgroup since: accepting and deciding stay with different people.
        Should.Throw<ApplicationRefusedException>(() => request.Approve("member-1", null, entitlement, false, Now)).Refusal.ShouldBe(ApplicationRefusal.AcceptorCannotDecide);
    }

    [Fact]
    public void KeepingTheOwnerNeedsNoNewAcceptance()
    {
        var entitlement = Registered();
        entitlement.AcceptOwnership("owner-1", false, entitlement.CurrentDraft!.ContentHash, Now);
        EntitlementChangeRequest.Propose(Guid.CreateVersion7(), entitlement, "maker", "Ready", Now).Approve("checker", null, entitlement, false, Now);

        entitlement.StartNewDraft(Settings(accessFor: AccessFor.Fixed(90)), 1, "author");
        entitlement.DraftNeedsAcceptance.ShouldBeFalse();
        Should.Throw<ApplicationRefusedException>(() => entitlement.AcceptOwnership("owner-1", false, entitlement.CurrentDraft!.ContentHash, Now))
            .Refusal.ShouldBe(ApplicationRefusal.NothingToAccept);
        var unchanged = EntitlementChangeRequest.Propose(Guid.CreateVersion7(), entitlement, "maker", "Longer", Now);
        unchanged.AcceptedBy.ShouldBeNull();
        unchanged.Approve("checker", null, entitlement, false, Now);
        entitlement.CurrentActive!.VersionNumber.ShouldBe(2);
        entitlement.Versions.Single(v => v.VersionNumber == 1).State.ShouldBe(SettingsVersionState.Superseded);

        // A new owner does need to accept.
        entitlement.StartNewDraft(Settings(owner: "owner-2"), 2, "author");
        entitlement.DraftNeedsAcceptance.ShouldBeTrue();
    }

    [Fact]
    public void AStoredRequestThatBreaksTheRulesIsRefused()
    {
        var hash = new string('a', 64);
        var id = Guid.CreateVersion7();
        Should.NotThrow(() => EntitlementChangeRequest.Restore(id, id, id, 1, hash, RoleHolderKind.Person, "owner-1", "owner-1", "maker", "r", Now, SecondPersonState.Approved, "checker", null, Now));
        foreach (var (checker, accepted) in new[] { ("owner-1", (string?)null), ("acceptor", "acceptor") })
        {
            Should.Throw<ApplicationRefusedException>(() => EntitlementChangeRequest.Restore(
                id, id, id, 1, hash, RoleHolderKind.Person, "owner-1", accepted, "maker", "r", Now, SecondPersonState.Approved, checker, null, Now))
                .Refusal.ShouldBe(ApplicationRefusal.InvalidRecord);
        }

        Should.Throw<ApplicationRefusedException>(() => EntitlementChangeRequest.Restore(
            id, id, id, 1, hash, RoleHolderKind.Group, "Not A Key", null, "maker", "r", Now, SecondPersonState.Proposed, null, null, null)).Refusal.ShouldBe(ApplicationRefusal.InvalidRecord);
    }
}
