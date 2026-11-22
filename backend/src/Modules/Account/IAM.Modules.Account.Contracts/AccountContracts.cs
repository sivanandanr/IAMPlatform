namespace IAM.Modules.Account.Contracts;

/// <summary>
/// An account as other modules see it (DL-089). Enum-like fields carry their stable names (<c>Primary</c>, <c>Active</c>),
/// so this contract does not depend on the Account module's domain types.
/// </summary>
public sealed record AccountRecord(Guid Id, Guid ApplicationId, string Type, string Name, string? TargetId, string Owner, string Status, bool Verified)
{
    /// <summary>The account id only: logging this object must not expose a person or a login.</summary>
    public override string ToString() => $"{nameof(AccountRecord)} {Id}";
}

/// <summary>Read-only view of recorded accounts for other modules (Request, Provisioning; ADR-0001: no cross-module table access).</summary>
public interface IAccountDirectory
{
    Task<AccountRecord?> FindAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The person's live (not closed) accounts in the application, at most 100.</summary>
    Task<IReadOnlyList<AccountRecord>> ForOwnerAsync(Guid applicationId, string owner, CancellationToken cancellationToken);

    /// <summary>Whether a live (not closed) account of the application has this name, compared ignoring case (DL-089).</summary>
    Task<bool> IsNameInUseAsync(Guid applicationId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Whether any recorded account of the application, closed or not, has this target id as its key compares it (DL-101): an
    /// undo never disables an object an account was recorded for.
    /// </summary>
    Task<bool> IsTargetRecordedAsync(Guid applicationId, string targetId, CancellationToken cancellationToken);
}

/// <summary>A proposed account name with the naming rule version it came from (evidence), or why there is none.</summary>
public sealed record ProposedAccountName(string? Name, string? Failure, int? RuleVersionNumber, string? RuleContentHash);

/// <summary>
/// Proposes the name of a new Primary or Secondary account from the application's active naming rule (DL-089c). The caller
/// passes the attribute values it holds (keys from the attribute catalog). A missing value or an invalid result fails closed;
/// so does a name another live account already has (DL-086).
/// </summary>
public interface IAccountNaming
{
    Task<ProposedAccountName> ProposeAsync(Guid applicationId, string accountType, IReadOnlyDictionary<string, string?> attributes, CancellationToken cancellationToken);
}

/// <summary>
/// An account provisioning created and the target confirmed (DL-094): who it belongs to, its type and name, and the target's own
/// identifier. The provisioning item that created it identifies it, so recording it again changes nothing.
/// </summary>
public sealed record CreatedAccount(Guid ProvisioningItemId, Guid ApplicationId, string Type, string Name, string TargetId, string Owner)
{
    /// <summary>The ids only: logging this object must not expose a person or a login.</summary>
    public override string ToString() => $"{nameof(CreatedAccount)} {ProvisioningItemId}";
}

/// <summary>Why recording a created account was refused: codes only (DL-094).</summary>
public sealed class AccountNotRecordedException(string code) : Exception($"The created account was not recorded: {code}.")
{
    public string Code { get; } = code;
}

/// <summary>
/// Records accounts that provisioning created (DL-094). Called only after the target confirmed the account, so it is recorded
/// active and verified; no second person, because the approved request authorised it. The same item records once.
/// </summary>
public interface IAccountProvisioning
{
    /// <summary>The recorded account's id; <see cref="AccountNotRecordedException"/> if it can't be recorded as given.</summary>
    Task<Guid> RecordCreatedAsync(CreatedAccount account, string correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// Why the account couldn't be recorded now (the same rules as <see cref="RecordCreatedAsync"/>: a valid account, its name
    /// and target id free, one live Primary), or null if it could. Records nothing (DL-096): provisioning asks before it
    /// changes the target, so the rules rarely refuse after the account exists there.
    /// </summary>
    Task<string?> CheckCreatableAsync(CreatedAccount account, CancellationToken cancellationToken);

    /// <summary>The account already recorded for a provisioning item, or null (DL-096).</summary>
    Task<Guid?> FindRecordedAsync(Guid provisioningItemId, CancellationToken cancellationToken);
}
