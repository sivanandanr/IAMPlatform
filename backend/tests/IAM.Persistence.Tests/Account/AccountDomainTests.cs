using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.Modules.Account.Application.Accounts;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Account.Domain.Naming;
using Shouldly;

namespace IAM.Persistence.Tests.Account;

/// <summary>Account and naming rule rules without a database (DL-089, spec v3.2 §3.1 to §3.3, CLAUDE.md rules 8 and 11).</summary>
public sealed class AccountDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid App = Guid.CreateVersion7();

    internal static NamingTemplate FirstDotLast(int maxLength = 20) => new(
        [
            NamePart.From("given_name", NameTransform.RemoveAccents, NameTransform.LettersDigitsOnly, NameTransform.Lower),
            NamePart.Text("."),
            NamePart.From("family_name", NameTransform.RemoveAccents, NameTransform.LettersDigitsOnly, NameTransform.Lower),
        ],
        maxLength);

    [Fact]
    public void ANameIsMadeFromAttributesWithTheFixedTransforms()
    {
        NamingTemplates.Evaluate(FirstDotLast(), new Dictionary<string, string?> { ["given_name"] = "Ána", ["family_name"] = "O'Lim-Ñuñez" })
            .ShouldBe(new NamingResult("ana.olimnunez", null));

        var initials = new NamingTemplate([NamePart.Text("adm-"), NamePart.FromFirst("given_name", 1, NameTransform.Lower), NamePart.From("family_name", NameTransform.Lower)], 30);
        NamingTemplates.Evaluate(initials, new Dictionary<string, string?> { ["given_name"] = "Ana", ["family_name"] = "Lim" }).Name.ShouldBe("adm-alim");
    }

    [Fact]
    public void AnyGapFailsClosedAndNothingIsGuessed()
    {
        NamingTemplates.Evaluate(FirstDotLast(), new Dictionary<string, string?> { ["given_name"] = "Ana" }).Failure.ShouldBe(NamingFailure.MissingAttribute);
        NamingTemplates.Evaluate(FirstDotLast(), new Dictionary<string, string?> { ["given_name"] = "Ana", ["family_name"] = "  " }).Failure.ShouldBe(NamingFailure.MissingAttribute);

        // Nothing left after the transforms, or too long: no name, never a shortened one.
        NamingTemplates.Evaluate(FirstDotLast(), new Dictionary<string, string?> { ["given_name"] = "李", ["family_name"] = "王" }).Failure.ShouldBe(NamingFailure.InvalidName);
        NamingTemplates.Evaluate(FirstDotLast(maxLength: 8), new Dictionary<string, string?> { ["given_name"] = "Ana", ["family_name"] = "Limerick" }).Failure.ShouldBe(NamingFailure.InvalidName);

        // Without LettersDigitsOnly a space or apostrophe stays, and the result isn't a valid name.
        var raw = new NamingTemplate([NamePart.From("given_name"), NamePart.From("family_name")], 40);
        NamingTemplates.Evaluate(raw, new Dictionary<string, string?> { ["given_name"] = "Ana", ["family_name"] = "O'Lim" }).Failure.ShouldBe(NamingFailure.InvalidName);
    }

    [Fact]
    public void TemplatesAreTypedAndBounded()
    {
        void Rejected(NamingTemplate template, string field) =>
            Should.Throw<AccountRejectedException>(() => NamingTemplates.Validate(template), field).Field.ShouldBe(field);

        Rejected(new NamingTemplate([], 20), "Parts");
        Rejected(new NamingTemplate([NamePart.Text("svc")], 20), "Parts");
        Rejected(new NamingTemplate([.. Enumerable.Repeat(NamePart.From("given_name"), 11)], 20), "Parts");
        Rejected(FirstDotLast(maxLength: 0), "MaxLength");
        Rejected(FirstDotLast(maxLength: 257), "MaxLength");
        Rejected(new NamingTemplate([NamePart.Text("a b"), NamePart.From("given_name")], 20), "Literal");
        Rejected(new NamingTemplate([NamePart.Text(new string('a', 21)), NamePart.From("given_name")], 20), "Literal");
        Rejected(new NamingTemplate([new NamePart("x", "given_name", [], null)], 20), "Parts");
        Rejected(new NamingTemplate([NamePart.From("GivenName")], 20), "Attribute");
        Rejected(new NamingTemplate([NamePart.From("given_name", (NameTransform)99)], 20), "Transforms");
        Rejected(new NamingTemplate([new NamePart(null, "given_name", [NameTransform.First], null)], 20), "Length");
        Rejected(new NamingTemplate([new NamePart(null, "given_name", [NameTransform.Lower], 3)], 20), "Length");
        Rejected(new NamingTemplate([NamePart.FromFirst("given_name", 65)], 20), "Length");
    }

    [Fact]
    public void TheStoredFormIsCanonicalAndTheHashCoversIt()
    {
        var stored = NamingTemplates.ToStored(FirstDotLast());
        NamingTemplates.ToStored(NamingTemplates.FromStored(stored)).ShouldBe(stored);
        foreach (var bad in new[] { stored.Replace("\"maxLength\":20", "\"maxLength\": 20", StringComparison.Ordinal), stored.Replace("Lower", "lower", StringComparison.Ordinal), "{}", "not json", stored + " " })
        {
            Should.Throw<AccountRefusedException>(() => NamingTemplates.FromStored(bad), bad).Refusal.ShouldBe(AccountRefusal.InvalidRecord);
        }

        var hash = NamingTemplates.ComputeContentHash(App, AccountType.Primary, 1, FirstDotLast());
        NamingTemplates.ComputeContentHash(App, AccountType.Secondary, 1, FirstDotLast()).ShouldNotBe(hash);
        NamingTemplates.ComputeContentHash(App, AccountType.Primary, 2, FirstDotLast()).ShouldNotBe(hash);
        NamingTemplates.ComputeContentHash(App, AccountType.Primary, 1, FirstDotLast(maxLength: 21)).ShouldNotBe(hash);
        NamingTemplates.ComputeContentHash(Guid.CreateVersion7(), AccountType.Primary, 1, FirstDotLast()).ShouldNotBe(hash);
    }

    [Fact]
    public void NamingRulesAreOnlyForPrimaryAndSecondaryAccounts()
    {
        Should.Throw<AccountRefusedException>(() => NamingRule.Create(App, AccountType.Service, FirstDotLast(), "author")).Refusal.ShouldBe(AccountRefusal.NoNamingRuleForType);
        Should.Throw<AccountRefusedException>(() => NamingRule.Create(App, AccountType.Shared, FirstDotLast(), "author")).Refusal.ShouldBe(AccountRefusal.NoNamingRuleForType);
        NamingRule.Create(App, AccountType.Secondary, FirstDotLast(), "author").CurrentDraft.ShouldNotBeNull();
    }

    [Fact]
    public void ARuleIsActivatedBySomeoneWhoNeitherProposedNorWroteIt()
    {
        var rule = NamingRule.Create(App, AccountType.Primary, FirstDotLast(), "author");
        var request = NamingRuleChangeRequest.Propose(Guid.CreateVersion7(), rule, "maker", "Ready", Now);
        Should.Throw<AccountRefusedException>(() => request.Approve("maker", null, rule, Now)).Refusal.ShouldBe(AccountRefusal.MakerCannotDecide);
        Should.Throw<AccountRefusedException>(() => request.Approve("author", null, rule, Now)).Refusal.ShouldBe(AccountRefusal.MakerCannotDecide);
        request.Approve("checker", null, rule, Now);
        rule.CurrentActive.ShouldNotBeNull().ActivatedByRequestId.ShouldBe(request.Id);

        // The next draft supersedes it only when approved.
        rule.SetDraft(FirstDotLast(maxLength: 30), 1, "author").ShouldBeTrue();
        rule.SetDraft(FirstDotLast(maxLength: 30), 2, "author").ShouldBeFalse("unchanged");
        var second = NamingRuleChangeRequest.Propose(Guid.CreateVersion7(), rule, "maker", "Longer", Now);
        rule.SetDraft(FirstDotLast(maxLength: 31), 2, "author");
        Should.Throw<AccountRefusedException>(() => second.Approve("checker", null, rule, Now)).Refusal.ShouldBe(AccountRefusal.DraftChanged);
    }

    [Fact]
    public void ALinkIsDecidedBySomeoneOtherThanTheMakerOrTheOwner()
    {
        var request = AccountLinkRequest.Propose(Guid.CreateVersion7(), App, AccountType.Primary, "ana.lim", "CN=Ana Lim,OU=Staff,DC=corp,DC=test", "ana", "admin", "HR record matches", Now);
        Should.Throw<AccountRefusedException>(() => request.Approve("admin", null, Now)).Refusal.ShouldBe(AccountRefusal.MakerCannotDecide);
        Should.Throw<AccountRefusedException>(() => request.Approve("ana", null, Now)).Refusal.ShouldBe(AccountRefusal.OwnerCannotDecide);
        var account = request.Approve("checker", "Checked", Now);
        account.Owner.ShouldBe("ana");
        account.NameKey.ShouldBe("ana.lim");
        account.Status.ShouldBe(AccountStatus.Active);
        account.Origin.ShouldBe(AccountOrigin.Linked);
        account.Verified.ShouldBeFalse("a person's word alone never verifies the details");
        account.LinkRequestId.ShouldBe(request.Id);
    }

    [Fact]
    public void LinkInputIsChecked()
    {
        AccountLinkRequest Propose(string name = "ana.lim", string target = "CN=x", string owner = "ana", AccountType type = AccountType.Primary) =>
            AccountLinkRequest.Propose(Guid.CreateVersion7(), App, type, name, target, owner, "admin", "r", Now);

        foreach (var name in new[] { "", ".ana", "ana lim", "ana@corp", new string('a', 257), "anä" })
        {
            Should.Throw<AccountRejectedException>(() => Propose(name: name), name).Field.ShouldBe("Name");
        }

        Should.Throw<AccountRejectedException>(() => Propose(target: " CN=x")).Field.ShouldBe("TargetId");
        Should.Throw<AccountRejectedException>(() => Propose(target: "CN=x\n")).Field.ShouldBe("TargetId");
        Should.Throw<AccountRejectedException>(() => Propose(owner: " ana")).Field.ShouldBe("Owner");
        Should.Throw<AccountRejectedException>(() => Propose(type: (AccountType)9)).Field.ShouldBe("Type");
        Should.Throw<AccountRejectedException>(() => AccountApi.ParseType("NHI")).Field.ShouldBe("Type");
        Should.Throw<AccountRejectedException>(() => AccountApi.ParseType("User")).Field.ShouldBe("Type");
        Should.Throw<AccountRejectedException>(() => AccountApi.ParseType("primary")).Field.ShouldBe("Type");
    }

    [Fact]
    public void AStoredLinkDecidedByTheOwnerIsDamaged() =>
        Should.Throw<AccountRefusedException>(() => AccountLinkRequest.Restore(
            Guid.CreateVersion7(), App, AccountType.Primary, "ana.lim", "CN=x", "ana", "admin", "r", Now, SecondPersonState.Approved, "ana", null, Now))
            .Refusal.ShouldBe(AccountRefusal.InvalidRecord);
}
