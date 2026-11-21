using System.Diagnostics;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Account.Contracts;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Account.Infrastructure.Persistence;
using IAM.Modules.Audit.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using AccountEntity = IAM.Modules.Account.Domain.Accounts.Account;

namespace IAM.Modules.Account.Infrastructure.Accounts;

/// <summary>
/// Records accounts provisioning created once the target confirmed them (DL-094): active, verified, origin Created, keyed by
/// the provisioning item, so a repeat returns the same account. The account rules apply as for a link: one live Primary per
/// person and application, names and target ids unique among live accounts (DL-086). Each record is audited in its
/// transaction with the request's correlation id. No permission check: the caller is the provisioning module acting on a
/// verified, authorised approval.
/// </summary>
public sealed class AccountProvisioningService(AccountDbContext db, IAuditWriter audit, TimeProvider clock, IIamDatabaseProvider provider) : IAccountProvisioning
{
    public const string Created = "account.account.created";
    public const string Target = "account.account";

    public async Task<string?> CheckCreatableAsync(CreatedAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            var created = Build(account);
            return await db.Accounts.AsNoTracking().AnyAsync(a => a.ProvisioningItemId == account.ProvisioningItemId, cancellationToken).ConfigureAwait(false)
                ? "ItemRecorded"
                : await RefusalAsync(created, cancellationToken).ConfigureAwait(false);
        }
        catch (AccountNotRecordedException refused)
        {
            return refused.Code;
        }
    }

    public async Task<Guid?> FindRecordedAsync(Guid provisioningItemId, CancellationToken cancellationToken) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.ProvisioningItemId == provisioningItemId)
            .Select(a => (Guid?)a.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<Guid> RecordCreatedAsync(CreatedAccount account, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        var created = Build(account);
        if (await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.ProvisioningItemId == account.ProvisioningItemId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return Same(existing, created) ? existing.Id : throw new AccountNotRecordedException("ItemRecordedDifferently");
        }

        if (await RefusalAsync(created, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            throw new AccountNotRecordedException(refusal);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Accounts.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (provider.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            // The same item recorded concurrently is the same account; anything else took the name, target or Primary first.
            if (await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.ProvisioningItemId == account.ProvisioningItemId, cancellationToken).ConfigureAwait(false) is { } raced
                && Same(raced, created))
            {
                return raced.Id;
            }

            throw new AccountNotRecordedException("Conflict");
        }

        var cause = Activity.Current?.TraceId.ToHexString();
        await audit.AppendAsync(
            tx.GetDbTransaction(),
            new AuditEventRecord
            {
                Action = Created,
                TargetType = Target,
                TargetId = created.Id.ToString(),
                Outcome = AuditOutcome.Succeeded,
                OccurredAtUtc = UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow()),
                CorrelationId = correlationId,
                CausationId = cause is not null && cause != correlationId ? cause : null,
                Payload =
                [
                    Field("provisioningItemId", account.ProvisioningItemId.ToString()),
                    Field("applicationId", created.ApplicationId.ToString()),
                    Field("type", created.Type.ToString()),
                    Field("name", created.Name),
                    Field("verified", "true"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created.Id;
    }

    private static AccountEntity Build(CreatedAccount account)
    {
        if (!Enum.GetNames<AccountType>().Contains(account.Type, StringComparer.Ordinal))
        {
            throw new AccountNotRecordedException("InvalidType");
        }

        try
        {
            return AccountEntity.FromProvisioning(account.ProvisioningItemId, account.ApplicationId, Enum.Parse<AccountType>(account.Type), account.Name, account.TargetId, account.Owner);
        }
        catch (AccountRejectedException)
        {
            throw new AccountNotRecordedException("InvalidAccount");
        }
    }

    /// <summary>The account rules a new account must meet among the application's live accounts, as a code, or null.</summary>
    private async Task<string?> RefusalAsync(AccountEntity created, CancellationToken cancellationToken)
    {
        var live = db.Accounts.AsNoTracking().Where(a => a.ApplicationId == created.ApplicationId && a.Status != AccountStatus.Closed);
        if (await live.AnyAsync(a => a.NameKey == created.NameKey, cancellationToken).ConfigureAwait(false))
        {
            return "NameInUse";
        }

        if (await live.AnyAsync(a => a.TargetId == created.TargetId, cancellationToken).ConfigureAwait(false))
        {
            return "TargetInUse";
        }

        return created.Type == AccountType.Primary && await live.AnyAsync(a => a.Owner == created.Owner && a.Type == AccountType.Primary, cancellationToken).ConfigureAwait(false)
            ? "PrimaryExists"
            : null;
    }

    private static bool Same(AccountEntity a, AccountEntity b) =>
        a.ApplicationId == b.ApplicationId && a.Type == b.Type && a.NameKey == b.NameKey
        && string.Equals(a.TargetId, b.TargetId, StringComparison.Ordinal) && string.Equals(a.Owner, b.Owner, StringComparison.Ordinal);

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);
}
