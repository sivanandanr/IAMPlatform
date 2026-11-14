using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Catalog.Domain.Applications;

/// <summary>
/// A kind of application role (AR-2.1): a catalog row, not a fixed column. User Admin, IT Admin and Technical custodian are
/// built in and can't be removed (DL-066c, IAM-FR-010 keeps IT Admin and technical custodian distinguishable). A
/// deployment's own type is the request itself: proposed by one person, usable only once a second person approves it
/// (the shared rules, <see cref="SecondPersonDecision"/>). Nothing is ever deleted.
/// </summary>
public sealed class ApplicationRoleType
{
    private ApplicationRoleType()
    {
        Code = string.Empty;
        DisplayName = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; }

    public string DisplayName { get; private set; }

    public RoleTypeState State { get; private set; }

    public string? Maker { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset? ProposedAtUtc { get; private set; }

    public string? Checker { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    /// <summary>Only built-in and approved types may be used in an application's settings.</summary>
    public bool IsUsable => State is RoleTypeState.BuiltIn or RoleTypeState.Approved;

    /// <summary>A type that blocks its code from being proposed again: built in, pending or approved.</summary>
    public bool HoldsCode => State is RoleTypeState.BuiltIn or RoleTypeState.Proposed or RoleTypeState.Approved;

    public static ApplicationRoleType Propose(string code, string displayName, string maker, string? reason, DateTimeOffset now)
    {
        CatalogRules.ValidateKey(code, "Code");
        CatalogRules.ValidateDisplayName(displayName, CatalogRules.RoleTypeDisplayNameMaxLength, "DisplayName");
        var decision = CatalogSecondPerson.Shared(() => SecondPersonDecision.Propose(maker, reason, now));
        var type = new ApplicationRoleType { Id = Guid.CreateVersion7(), Code = code, DisplayName = displayName };
        type.CopyFrom(decision);
        return type;
    }

    /// <summary>Refuses unless <paramref name="checker"/> could decide now: still proposed and not the maker.</summary>
    public void RequireDecider(string checker) => CatalogSecondPerson.Shared(() => Decision().RequireDecider(checker));

    public void Approve(string checker, string? reason, DateTimeOffset now) => Decide(d => d.Approve(checker, reason, now));

    public void Reject(string checker, string? reason, DateTimeOffset now) => Decide(d => d.Reject(checker, reason, now));

    public void Withdraw(string actor, string? reason, DateTimeOffset now) => Decide(d => d.Withdraw(actor, reason, now));

    /// <summary>Checks a stored row's shape (as the database CHECK does); a damaged row is refused.</summary>
    public void RequireValid()
    {
        if (State == RoleTypeState.BuiltIn)
        {
            if (Maker is not null || Reason is not null || ProposedAtUtc is not null || Checker is not null || DecisionReason is not null || DecidedAtUtc is not null)
            {
                throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
            }

            return;
        }

        _ = Decision();
    }

    private void Decide(Action<SecondPersonDecision> step)
    {
        var decision = Decision();
        CatalogSecondPerson.Shared(() => step(decision));
        CopyFrom(decision);
    }

    private SecondPersonDecision Decision()
    {
        if (State == RoleTypeState.BuiltIn || Maker is null || Reason is null || ProposedAtUtc is null)
        {
            throw new ApplicationRefusedException(State == RoleTypeState.BuiltIn ? ApplicationRefusal.NotProposed : ApplicationRefusal.InvalidRecord);
        }

        var state = State switch
        {
            RoleTypeState.Proposed => SecondPersonState.Proposed,
            RoleTypeState.Approved => SecondPersonState.Approved,
            RoleTypeState.Rejected => SecondPersonState.Rejected,
            RoleTypeState.Withdrawn => SecondPersonState.Withdrawn,
            _ => (SecondPersonState)0,
        };
        try
        {
            return SecondPersonDecision.Restore(Maker, Reason, ProposedAtUtc.Value, state, Checker, DecisionReason, DecidedAtUtc);
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord, ex);
        }
    }

    private void CopyFrom(SecondPersonDecision decision)
    {
        State = decision.State switch
        {
            SecondPersonState.Proposed => RoleTypeState.Proposed,
            SecondPersonState.Approved => RoleTypeState.Approved,
            SecondPersonState.Rejected => RoleTypeState.Rejected,
            _ => RoleTypeState.Withdrawn,
        };
        Maker = decision.Maker;
        Reason = decision.Reason;
        ProposedAtUtc = decision.ProposedAtUtc;
        Checker = decision.Checker;
        DecisionReason = decision.DecisionReason;
        DecidedAtUtc = decision.DecidedAtUtc;
    }
}

/// <summary>The built-in role types, seeded by the ApplicationCatalog migrations with these fixed ids (DL-066c).</summary>
public static class BuiltInRoleTypes
{
    public static readonly Guid UserAdmin = new("0193a1f0-0000-7000-8000-000000000001");
    public static readonly Guid ItAdmin = new("0193a1f0-0000-7000-8000-000000000002");
    public static readonly Guid TechnicalCustodian = new("0193a1f0-0000-7000-8000-000000000003");

    public static IReadOnlyList<(Guid Id, string Code, string DisplayName)> All { get; } =
    [
        (UserAdmin, "user_admin", "User Admin"),
        (ItAdmin, "it_admin", "IT Admin"),
        (TechnicalCustodian, "technical_custodian", "Technical custodian"),
    ];
}
