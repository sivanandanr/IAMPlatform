using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Account.Domain.Accounts;

/// <summary>
/// One account in one application (spec v3.2 §3.1, §3.2, DL-084f, DL-089): its type, its name (the login), the target's own
/// identifier, and the person it belongs to (the holder of a Primary or Secondary account, the owner of a Service or Shared
/// one). Who it belongs to, its type and how it came to be recorded never change. Details a person entered stay "not
/// verified" until the target confirms them (CLAUDE.md "Manual work").
/// </summary>
public sealed class Account
{
    private Account()
    {
        Name = string.Empty;
        NameKey = string.Empty;
        Owner = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ApplicationId { get; private set; }

    public AccountType Type { get; private set; }

    /// <summary>The account's name in the target, as it is spelled there.</summary>
    public string Name { get; private set; }

    /// <summary><see cref="Name"/> in lower case: names are compared ignoring case, so this is what must be unique.</summary>
    public string NameKey { get; private set; }

    /// <summary>The target's own identifier for the account; none until it is known.</summary>
    public string? TargetId { get; private set; }

    /// <summary>The person the account belongs to: its holder (Primary, Secondary) or owner (Service, Shared).</summary>
    public string Owner { get; private set; }

    public AccountStatus Status { get; private set; }

    public AccountOrigin Origin { get; private set; }

    /// <summary>The approved link request that recorded a linked account.</summary>
    public Guid? LinkRequestId { get; private set; }

    /// <summary>The provisioning item that created an account IAM created (DL-094).</summary>
    public Guid? ProvisioningItemId { get; private set; }

    /// <summary>Whether the target has confirmed the details; a person's word alone never does.</summary>
    public bool Verified { get; private set; }

    /// <summary>
    /// Records an account provisioning created, once the target confirmed it (DL-094): active and verified, from the
    /// provisioning item that created it. The values are checked as a link's are.
    /// </summary>
    public static Account FromProvisioning(Guid provisioningItemId, Guid applicationId, AccountType type, string name, string targetId, string owner)
    {
        if (provisioningItemId == Guid.Empty || applicationId == Guid.Empty)
        {
            throw new AccountRejectedException("ProvisioningItemId", "is required");
        }

        AccountRules.ValidateType(type);
        AccountRules.ValidateName(name);
        AccountRules.ValidateTargetId(targetId);
        AccountRules.ValidateOwner(owner);
        return new Account
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = applicationId,
            Type = type,
            Name = name,
            NameKey = AccountRules.NameKey(name),
            TargetId = targetId,
            Owner = owner,
            Status = AccountStatus.Active,
            Origin = AccountOrigin.Created,
            ProvisioningItemId = provisioningItemId,
            Verified = true,
        };
    }

    /// <summary>Records an existing account exactly as an approved link request names it. Only the request calls this.</summary>
    internal static Account FromLink(AccountLinkRequest request) => new()
    {
        Id = Guid.CreateVersion7(),
        ApplicationId = request.ApplicationId,
        Type = request.Type,
        Name = request.Name,
        NameKey = AccountRules.NameKey(request.Name),
        TargetId = request.TargetId,
        Owner = request.Owner,
        Status = AccountStatus.Active,
        Origin = AccountOrigin.Linked,
        LinkRequestId = request.Id,
        Verified = false,
    };
}

/// <summary>
/// A request to record that an existing account in an application belongs to a person (spec v3.2 §3.3, DL-089a): an
/// administrator proposes it and a second person approves it. The maker can't decide it, nor can the person the account would
/// belong to. Matching never guesses: what is linked is exactly what the request names.
/// </summary>
public sealed class AccountLinkRequest
{
    private readonly SecondPersonDecision _decision;

    private AccountLinkRequest(Guid id, Guid applicationId, AccountType type, string name, string targetId, string owner, SecondPersonDecision decision)
    {
        Id = id;
        ApplicationId = applicationId;
        Type = type;
        Name = name;
        TargetId = targetId;
        Owner = owner;
        _decision = decision;
    }

    public Guid Id { get; }

    public Guid ApplicationId { get; }

    public AccountType Type { get; }

    public string Name { get; }

    public string TargetId { get; }

    public string Owner { get; }

    public string Maker => _decision.Maker;

    public string Reason => _decision.Reason;

    public DateTimeOffset ProposedAtUtc => _decision.ProposedAtUtc;

    public SecondPersonState State => _decision.State;

    public string? Checker => _decision.Checker;

    public string? DecisionReason => _decision.DecisionReason;

    public DateTimeOffset? DecidedAtUtc => _decision.DecidedAtUtc;

    public static AccountLinkRequest Propose(Guid id, Guid applicationId, AccountType type, string name, string targetId, string owner, string maker, string? reason, DateTimeOffset now)
    {
        if (id == Guid.Empty || applicationId == Guid.Empty)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord);
        }

        AccountRules.ValidateType(type);
        AccountRules.ValidateName(name);
        AccountRules.ValidateTargetId(targetId);
        AccountRules.ValidateOwner(owner);
        var decision = AccountSecondPerson.Shared(() => SecondPersonDecision.Propose(maker, reason, now));
        return new AccountLinkRequest(id, applicationId, type, name, targetId, owner, decision);
    }

    /// <summary>Rebuilds a stored request; a record these rules forbid is refused as damaged.</summary>
    public static AccountLinkRequest Restore(
        Guid id,
        Guid applicationId,
        AccountType type,
        string name,
        string targetId,
        string owner,
        string maker,
        string reason,
        DateTimeOffset proposedAtUtc,
        SecondPersonState state,
        string? checker,
        string? decisionReason,
        DateTimeOffset? decidedAtUtc)
    {
        try
        {
            AccountRules.ValidateType(type);
            AccountRules.ValidateName(name);
            AccountRules.ValidateTargetId(targetId);
            AccountRules.ValidateOwner(owner);
        }
        catch (AccountRejectedException ex)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord, ex);
        }

        if (id == Guid.Empty || applicationId == Guid.Empty
            || (state is SecondPersonState.Approved or SecondPersonState.Rejected && string.Equals(checker, owner, StringComparison.Ordinal)))
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord);
        }

        SecondPersonDecision decision;
        try
        {
            decision = SecondPersonDecision.Restore(maker, reason, proposedAtUtc, state, checker, decisionReason, decidedAtUtc);
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord, ex);
        }

        return new AccountLinkRequest(id, applicationId, type, name, targetId, owner, decision);
    }

    /// <summary>Refuses unless <paramref name="checker"/> could decide now: still proposed, not the maker, not the account's owner.</summary>
    public void RequireDecider(string checker)
    {
        AccountSecondPerson.Shared(() => _decision.RequireDecider(checker));
        if (string.Equals(checker, Owner, StringComparison.Ordinal))
        {
            throw new AccountRefusedException(AccountRefusal.OwnerCannotDecide);
        }
    }

    /// <summary>Approves the request and returns the account it records. The caller checks uniqueness and stores both together.</summary>
    public Account Approve(string checker, string? reason, DateTimeOffset now)
    {
        RequireDecider(checker);
        AccountSecondPerson.Shared(() => SecondPersonDecision.RequireOptionalReason(reason));
        AccountSecondPerson.Shared(() => _decision.Approve(checker, reason, now));
        return Account.FromLink(this);
    }

    public void Reject(string checker, string? reason, DateTimeOffset now)
    {
        RequireDecider(checker);
        AccountSecondPerson.Shared(() => _decision.Reject(checker, reason, now));
    }

    public void Withdraw(string actor, string? reason, DateTimeOffset now) => AccountSecondPerson.Shared(() => _decision.Withdraw(actor, reason, now));
}
