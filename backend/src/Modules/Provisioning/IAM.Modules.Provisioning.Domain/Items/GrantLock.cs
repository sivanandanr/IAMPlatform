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

    /// <summary>
    /// The run of <see cref="HeldBy"/> that holds it (DL-101): a run that lost its lease can't renew or release the lock of the
    /// run that took its item over.
    /// </summary>
    public Guid ClaimToken { get; private set; }

    public DateTimeOffset UntilUtc { get; private set; }

    public static GrantLock Take(Guid accountId, Guid entitlementId, Guid itemId, Guid claimToken, DateTimeOffset untilUtc)
    {
        if (accountId == Guid.Empty || entitlementId == Guid.Empty)
        {
            throw new ArgumentException("A grant lock names an account and an entitlement.", nameof(accountId));
        }

        var taken = new GrantLock { AccountId = accountId, EntitlementId = entitlementId };
        taken.Hold(itemId, claimToken, untilUtc);
        return taken;
    }

    /// <summary>Held by this run until <paramref name="untilUtc"/> (taken, renewed, or released with a time already past).</summary>
    public void Hold(Guid itemId, Guid claimToken, DateTimeOffset untilUtc)
    {
        if (itemId == Guid.Empty || claimToken == Guid.Empty)
        {
            throw new ArgumentException("A grant lock names its holder and the holder's run.", nameof(itemId));
        }

        HeldBy = itemId;
        ClaimToken = claimToken;
        UntilUtc = untilUtc;
    }

    /// <summary>Whether this run holds it now.</summary>
    public bool IsHeldBy(Guid itemId, Guid claimToken, DateTimeOffset now) => HeldBy == itemId && ClaimToken == claimToken && UntilUtc >= now;
}
