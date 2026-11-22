namespace IAM.Modules.Provisioning.Domain.Items;

/// <summary>
/// Who may change one entitlement on one existing account right now (DL-100): a run adding it and an undo removing it take
/// turns, so an undo can see whether another item still holds the access before it takes it away. Held until
/// <see cref="UntilUtc"/>; a runner that stops lets it run out.
/// </summary>
public sealed class GrantLock
{
    private GrantLock()
    {
    }

    public Guid AccountId { get; private set; }

    public Guid EntitlementId { get; private set; }

    /// <summary>The provisioning item holding it.</summary>
    public Guid HeldBy { get; private set; }

    public DateTimeOffset UntilUtc { get; private set; }

    public static GrantLock Take(Guid accountId, Guid entitlementId, Guid itemId, DateTimeOffset untilUtc)
    {
        if (accountId == Guid.Empty || entitlementId == Guid.Empty || itemId == Guid.Empty)
        {
            throw new ArgumentException("A grant lock names an account, an entitlement and its holder.", nameof(accountId));
        }

        return new GrantLock { AccountId = accountId, EntitlementId = entitlementId, HeldBy = itemId, UntilUtc = untilUtc };
    }
}
