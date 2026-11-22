using IAM.Modules.Account.Contracts;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Account.Domain.Naming;
using IAM.Modules.Account.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using AccountEntity = IAM.Modules.Account.Domain.Accounts.Account;

namespace IAM.Modules.Account.Infrastructure.Accounts;

/// <summary>Recorded accounts for other modules (DL-089). Reads only; no permission check, as for other module contracts.</summary>
public sealed class AccountDirectory(AccountDbContext db) : IAccountDirectory
{
    private const int MaxPerOwner = 100;

    public async Task<AccountRecord?> FindAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken).ConfigureAwait(false) is { } account ? Record(account) : null;

    public async Task<IReadOnlyList<AccountRecord>> ForOwnerAsync(Guid applicationId, string owner, CancellationToken cancellationToken)
    {
        AccountRules.ValidateOwner(owner);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.ApplicationId == applicationId && a.Owner == owner && a.Status != AccountStatus.Closed)
            .OrderBy(a => a.NameKey)
            .Take(MaxPerOwner)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. accounts.Select(Record)];
    }

    public Task<bool> IsNameInUseAsync(Guid applicationId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = AccountRules.NameKey(name);
        return db.Accounts.AnyAsync(a => a.ApplicationId == applicationId && a.NameKey == key && a.Status != AccountStatus.Closed, cancellationToken);
    }

    public Task<bool> IsTargetRecordedAsync(Guid applicationId, string targetId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        var key = AccountRules.TargetIdKey(targetId);
        return db.Accounts.AnyAsync(a => a.ApplicationId == applicationId && a.TargetIdKey == key, cancellationToken);
    }

    private static AccountRecord Record(AccountEntity a) => new(a.Id, a.ApplicationId, a.Type.ToString(), a.Name, a.TargetId, a.Owner, a.Status.ToString(), a.Verified);
}

/// <summary>
/// Proposes a new account's name from the active naming rule (DL-089c). Fails closed: no active rule, a missing attribute,
/// an invalid result, or a name a live account of the application already has (DL-086) all give no name.
/// </summary>
public sealed class AccountNaming(AccountDbContext db) : IAccountNaming
{
    public const string NoRule = "NoRule";
    public const string NameInUse = "NameInUse";

    public async Task<ProposedAccountName> ProposeAsync(Guid applicationId, string accountType, IReadOnlyDictionary<string, string?> attributes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (!Enum.GetNames<AccountType>().Contains(accountType, StringComparer.Ordinal))
        {
            return new ProposedAccountName(null, NoRule, null, null);
        }

        var type = Enum.Parse<AccountType>(accountType);
        var active = await db.NamingRuleVersions.AsNoTracking()
            .Where(v => v.State == NamingRuleVersionState.Active)
            .Join(db.NamingRules.Where(r => r.ApplicationId == applicationId && r.AccountType == type), v => v.NamingRuleId, r => r.Id, (v, _) => v)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            return new ProposedAccountName(null, NoRule, null, null);
        }

        var result = NamingTemplates.Evaluate(active.ToTemplate(), attributes);
        if (!result.Succeeded)
        {
            return new ProposedAccountName(null, result.Failure.ToString(), active.VersionNumber, active.ContentHash);
        }

        var key = AccountRules.NameKey(result.Name!);
        var taken = await db.Accounts.AnyAsync(a => a.ApplicationId == applicationId && a.NameKey == key && a.Status != AccountStatus.Closed, cancellationToken).ConfigureAwait(false);
        return taken
            ? new ProposedAccountName(null, NameInUse, active.VersionNumber, active.ContentHash)
            : new ProposedAccountName(result.Name, null, active.VersionNumber, active.ContentHash);
    }
}
