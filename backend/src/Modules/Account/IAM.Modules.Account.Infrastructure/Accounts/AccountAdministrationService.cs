using System.Diagnostics;
using System.Globalization;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Account.Application.Accounts;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Account.Domain.Naming;
using IAM.Modules.Account.Infrastructure.Persistence;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using AccountEntity = IAM.Modules.Account.Domain.Accounts.Account;

namespace IAM.Modules.Account.Infrastructure.Accounts;

/// <summary>Audit action codes of the Account module (registered with the audit writer by the module).</summary>
public static class AccountAuditActions
{
    public const string LinkProposed = "account.link.proposed";
    public const string LinkApproved = "account.link.approved";
    public const string LinkRejected = "account.link.rejected";
    public const string LinkWithdrawn = "account.link.withdrawn";
    public const string LinkRefused = "account.link.refused";
    public const string NamingDraftSet = "account.naming_rule.draft_set";
    public const string NamingProposed = "account.naming_rule.proposed";
    public const string NamingApproved = "account.naming_rule.approved";
    public const string NamingRejected = "account.naming_rule.rejected";
    public const string NamingWithdrawn = "account.naming_rule.withdrawn";
    public const string NamingRefused = "account.naming_rule.refused";
    public const string LinkTarget = "account.link_request";
    public const string NamingTarget = "account.naming_rule";

    public const string ReadAction = AccessActions.AccountsRead;
    public const string ManageAction = AccessActions.AccountsManage;
    public const string ApproveAction = AccessActions.AccountsApprove;

    public static IReadOnlyList<string> All { get; } =
    [
        LinkProposed, LinkApproved, LinkRejected, LinkWithdrawn, LinkRefused,
        NamingDraftSet, NamingProposed, NamingApproved, NamingRejected, NamingWithdrawn, NamingRefused,
    ];
}

/// <summary>
/// Account administration (DL-089). Each change and its audit event are saved in one transaction (ADR-0018); every refused
/// write, including a missing permission, is audited as denied with its code. Every call checks its permission here as well
/// as at the endpoint (ADR-0008).
/// </summary>
public sealed class AccountAdministrationService(
    AccountDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions,
    IApplicationApproverDirectory applications,
    IAttributeCatalog attributes) : IAccountAdministration
{
    private const string ProposedState = nameof(SecondPersonState.Proposed);
    private const int MaxListed = 500;

    public async Task<AccountView?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await RequireAsync(AccountAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken).ConfigureAwait(false);
        return account is null ? null : View(account);
    }

    public async Task<IReadOnlyList<AccountView>> ListAccountsAsync(Guid applicationId, string? owner, CancellationToken cancellationToken)
    {
        await RequireAsync(AccountAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        if (owner is not null)
        {
            AccountRules.ValidateOwner(owner);
        }

        var query = db.Accounts.AsNoTracking().Where(a => a.ApplicationId == applicationId);
        if (owner is not null)
        {
            query = query.Where(a => a.Owner == owner);
        }

        var accounts = await query.OrderBy(a => a.NameKey).Take(MaxListed).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. accounts.Select(View)];
    }

    public async Task<AccountLinkRequestView> ProposeLinkAsync(Guid applicationId, string type, string name, string targetId, string owner, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AccountLinkRequest proposed;
        try
        {
            await RequireAsync(AccountAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            proposed = AccountLinkRequest.Propose(Guid.CreateVersion7(), applicationId, AccountApi.ParseType(type), name, targetId, owner, actor, reason, Now());
            await RequireApplicationActiveAsync(applicationId, cancellationToken).ConfigureAwait(false);
            var key = AccountRules.NameKey(proposed.Name);
            if (await db.LinkRequests.AnyAsync(r => r.ApplicationId == applicationId && r.NameKey == key && r.State == ProposedState, cancellationToken).ConfigureAwait(false))
            {
                throw new AccountRefusedException(AccountRefusal.AlreadyPending);
            }

            await RequireFreeAsync(proposed, cancellationToken).ConfigureAwait(false);
        }
        catch (AccountRefusedException refusal)
        {
            await RefuseAsync(tx, AccountAuditActions.LinkRefused, AccountAuditActions.LinkTarget, null, "propose", refusal, Field("applicationId", applicationId.ToString()), cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.LinkRequests.Add(new AccountLinkRequestRow
        {
            Id = proposed.Id,
            ApplicationId = proposed.ApplicationId,
            Type = proposed.Type.ToString(),
            Name = proposed.Name,
            NameKey = AccountRules.NameKey(proposed.Name),
            TargetId = proposed.TargetId,
            Owner = proposed.Owner,
            Maker = proposed.Maker,
            Reason = proposed.Reason,
            ProposedAtUtc = proposed.ProposedAtUtc,
            State = proposed.State.ToString(),
        });
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), LinkRecord(AccountAuditActions.LinkProposed, proposed, proposed.Reason, null), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(proposed, null);
    }

    public async Task<AccountLinkRequestView?> FindLinkRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await RequireAsync(AccountAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var row = await db.LinkRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var accountId = await db.Accounts.AsNoTracking().Where(a => a.LinkRequestId == requestId).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return View(Restore(row), accountId);
    }

    /// <summary>
    /// Records the account exactly as the request names it. The application must still be active, and the person must not have
    /// gained a Primary account, nor anyone else the name or target id, since it was proposed.
    /// </summary>
    public async Task<AccountLinkRequestView> ApproveLinkAsync(Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadLinkAsync(requestId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AccountLinkRequest request;
        AccountEntity account;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor);
            await RequireAsync(AccountAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            await RequirePersonAsync(request.Maker, AccountAuditActions.ManageAction, AccountRefusal.MakerNoLongerAllowed, cancellationToken).ConfigureAwait(false);
            await RequireApplicationActiveAsync(request.ApplicationId, cancellationToken).ConfigureAwait(false);
            await RequireFreeAsync(request, cancellationToken).ConfigureAwait(false);
            account = request.Approve(actor, reason, Now());
        }
        catch (AccountRefusedException refusal)
        {
            await RefuseAsync(tx, AccountAuditActions.LinkRefused, AccountAuditActions.LinkTarget, requestId, "approve", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        db.Accounts.Add(account);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), LinkRecord(AccountAuditActions.LinkApproved, request, request.DecisionReason, account.Id), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request, account.Id);
    }

    public Task<AccountLinkRequestView> RejectLinkAsync(Guid requestId, string reason, CancellationToken cancellationToken) =>
        DecideLinkAsync(requestId, "reject", AccountAuditActions.LinkRejected, async (request, actor) =>
        {
            request.RequireDecider(actor);
            await RequireAsync(AccountAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            request.Reject(actor, reason, Now());
        }, cancellationToken);

    public Task<AccountLinkRequestView> WithdrawLinkAsync(Guid requestId, string? reason, CancellationToken cancellationToken) =>
        DecideLinkAsync(requestId, "withdraw", AccountAuditActions.LinkWithdrawn, async (request, actor) =>
        {
            await RequireAsync(AccountAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            request.Withdraw(actor, reason, Now());
        }, cancellationToken);

    public async Task<NamingRuleView?> FindNamingRuleAsync(Guid applicationId, string accountType, CancellationToken cancellationToken)
    {
        await RequireAsync(AccountAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var type = AccountApi.ParseType(accountType);
        var rule = await Current(db.NamingRules).SingleOrDefaultAsync(r => r.ApplicationId == applicationId && r.AccountType == type, cancellationToken).ConfigureAwait(false);
        return rule is null ? null : View(rule);
    }

    public async Task<NamingRuleView> SetNamingDraftAsync(
        Guid applicationId, string accountType, long? expectedConcurrencyVersion, IReadOnlyList<NamePartText?> parts, int maxLength, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var type = AccountApi.ParseType(accountType);
        var rule = await Current(db.NamingRules).SingleOrDefaultAsync(r => r.ApplicationId == applicationId && r.AccountType == type, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            await RequireAsync(AccountAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            var template = AccountApi.ToTemplate(parts, maxLength);
            NamingTemplates.Validate(template);
            await RequirePublishedAttributesAsync(template, cancellationToken).ConfigureAwait(false);
            if (rule is null)
            {
                await RequireApplicationActiveAsync(applicationId, cancellationToken).ConfigureAwait(false);
                rule = NamingRule.Create(applicationId, type, template, actor);
                db.NamingRules.Add(rule);
                changed = true;
            }
            else
            {
                if (db.Entry(rule).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
                {
                    throw new AccountConflictException();
                }

                await RequireNothingPendingAsync(rule.Id, cancellationToken).ConfigureAwait(false);
                var highest = await db.NamingRuleVersions.Where(v => v.NamingRuleId == rule.Id).MaxAsync(v => v.VersionNumber, cancellationToken).ConfigureAwait(false);
                changed = rule.SetDraft(template, highest, actor);
                Touch(rule);
            }
        }
        catch (AccountRefusedException refusal)
        {
            await RefuseAsync(tx, AccountAuditActions.NamingRefused, AccountAuditActions.NamingTarget, rule?.Id, "set_draft", refusal, Field("applicationId", applicationId.ToString()), cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), RuleRecord(AccountAuditActions.NamingDraftSet, rule, rule.CurrentDraft!, null, null), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return View(rule);
    }

    public async Task<NamingRuleChangeRequestView> ProposeNamingRuleAsync(Guid applicationId, string accountType, string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var type = AccountApi.ParseType(accountType);
        var rule = await Current(db.NamingRules).SingleOrDefaultAsync(r => r.ApplicationId == applicationId && r.AccountType == type, cancellationToken).ConfigureAwait(false)
            ?? throw new AccountNotFoundException();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        NamingRuleChangeRequest proposed;
        try
        {
            await RequireAsync(AccountAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(rule.Id, cancellationToken).ConfigureAwait(false);
            proposed = NamingRuleChangeRequest.Propose(Guid.CreateVersion7(), rule, actor, reason, Now());
        }
        catch (AccountRefusedException refusal)
        {
            await RefuseAsync(tx, AccountAuditActions.NamingRefused, AccountAuditActions.NamingTarget, rule.Id, "propose", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.NamingRequests.Add(new NamingRuleChangeRequestRow
        {
            Id = proposed.Id,
            NamingRuleId = proposed.NamingRuleId,
            VersionId = proposed.VersionId,
            VersionNumber = proposed.VersionNumber,
            ContentHash = proposed.ContentHash,
            Maker = proposed.Maker,
            Reason = proposed.Reason,
            ProposedAtUtc = proposed.ProposedAtUtc,
            State = proposed.State.ToString(),
        });
        Touch(rule);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RuleRecord(AccountAuditActions.NamingProposed, rule, rule.CurrentDraft!, proposed, proposed.Reason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(proposed);
    }

    public async Task<NamingRuleChangeReview?> ReviewNamingRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await RequireAsync(AccountAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var row = await db.NamingRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var rule = await Current(db.NamingRules).SingleAsync(r => r.Id == row.NamingRuleId, cancellationToken).ConfigureAwait(false);
        var draft = rule.CurrentDraft;
        var named = draft is not null && draft.Id == row.VersionId && string.Equals(draft.ContentHash, row.ContentHash, StringComparison.Ordinal);
        return new NamingRuleChangeReview(View(Restore(row)), named ? View(draft!) : null);
    }

    public async Task<NamingRuleChangeRequestView> ApproveNamingRuleAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadNamingAsync(requestId, cancellationToken).ConfigureAwait(false);
        var rule = await Current(db.NamingRules).SingleAsync(r => r.Id == row.NamingRuleId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        NamingRuleChangeRequest request;
        NamingRuleVersion draft;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor);
            await RequireAsync(AccountAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            await RequirePersonAsync(request.Maker, AccountAuditActions.ManageAction, AccountRefusal.MakerNoLongerAllowed, cancellationToken).ConfigureAwait(false);
            draft = rule.CurrentDraft ?? throw new AccountRefusedException(AccountRefusal.NoDraft);
            if (string.Equals(db.Entry(draft).Property<string>(AuditColumns.CreatedBy).CurrentValue, actor, StringComparison.Ordinal))
            {
                throw new AccountRefusedException(AccountRefusal.MakerCannotDecide);
            }

            if (!string.Equals(reviewedContentHash, request.ContentHash, StringComparison.Ordinal))
            {
                throw new AccountRefusedException(AccountRefusal.DraftChanged);
            }

            // The attributes must still be published: one retired since the draft was written can't name accounts.
            await RequirePublishedAttributesAsync(draft.ToTemplate(), cancellationToken).ConfigureAwait(false);
            request.Approve(actor, reason, rule, Now());
        }
        catch (AccountRefusedException refusal)
        {
            await RefuseAsync(tx, AccountAuditActions.NamingRefused, AccountAuditActions.NamingTarget, rule.Id, "approve", refusal, Field("requestId", requestId.ToString()), cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await db.SaveVersionsInOrderAsync(draft, SaveAsync, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), RuleRecord(AccountAuditActions.NamingApproved, rule, draft, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public Task<NamingRuleChangeRequestView> RejectNamingRuleAsync(Guid requestId, string reason, CancellationToken cancellationToken) =>
        DecideNamingAsync(requestId, "reject", AccountAuditActions.NamingRejected, async (request, actor) =>
        {
            request.RequireDecider(actor);
            await RequireAsync(AccountAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            request.Reject(actor, reason, Now());
        }, cancellationToken);

    public Task<NamingRuleChangeRequestView> WithdrawNamingRuleAsync(Guid requestId, string? reason, CancellationToken cancellationToken) =>
        DecideNamingAsync(requestId, "withdraw", AccountAuditActions.NamingWithdrawn, async (request, actor) =>
        {
            await RequireAsync(AccountAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            request.Withdraw(actor, reason, Now());
        }, cancellationToken);

    private async Task<AccountLinkRequestView> DecideLinkAsync(Guid requestId, string step, string action, Func<AccountLinkRequest, string, Task> decide, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadLinkAsync(requestId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AccountLinkRequest request;
        try
        {
            request = Restore(row);
            await decide(request, actor).ConfigureAwait(false);
        }
        catch (AccountRefusedException refusal)
        {
            await RefuseAsync(tx, AccountAuditActions.LinkRefused, AccountAuditActions.LinkTarget, requestId, step, refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), LinkRecord(action, request, request.DecisionReason, null), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request, null);
    }

    private async Task<NamingRuleChangeRequestView> DecideNamingAsync(Guid requestId, string step, string action, Func<NamingRuleChangeRequest, string, Task> decide, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadNamingAsync(requestId, cancellationToken).ConfigureAwait(false);
        var rule = await Current(db.NamingRules).SingleAsync(r => r.Id == row.NamingRuleId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        NamingRuleChangeRequest request;
        try
        {
            request = Restore(row);
            await decide(request, actor).ConfigureAwait(false);
        }
        catch (AccountRefusedException refusal)
        {
            await RefuseAsync(tx, AccountAuditActions.NamingRefused, AccountAuditActions.NamingTarget, rule.Id, step, refusal, Field("requestId", requestId.ToString()), cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        var version = rule.Versions.SingleOrDefault(v => v.Id == request.VersionId);
        await audit.AppendAsync(tx.GetDbTransaction(), version is null
            ? Event(action, AccountAuditActions.NamingTarget, rule.Id, AuditOutcome.Succeeded, request.DecisionReason, Field("requestId", request.Id.ToString()), Field("state", request.State.ToString()))
            : RuleRecord(action, rule, version, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    private async Task RequireApplicationActiveAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        if (await applications.FindActiveAsync(applicationId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new AccountRefusedException(AccountRefusal.ApplicationNotActive);
        }
    }

    /// <summary>A naming rule may only use published attributes of the shared catalog (ADR-0015).</summary>
    private async Task RequirePublishedAttributesAsync(NamingTemplate template, CancellationToken cancellationToken)
    {
        foreach (var key in NamingTemplates.Attributes(template))
        {
            if (await attributes.FindPublishedByKeyAsync(key, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new AccountRefusedException(AccountRefusal.UnknownAttribute);
            }
        }
    }

    /// <summary>One live Primary per person per application; live names (ignoring case) and target ids unique per application.</summary>
    private async Task RequireFreeAsync(AccountLinkRequest request, CancellationToken cancellationToken)
    {
        var live = db.Accounts.Where(a => a.ApplicationId == request.ApplicationId && a.Status != AccountStatus.Closed);
        if (request.Type == AccountType.Primary
            && await live.AnyAsync(a => a.Type == AccountType.Primary && a.Owner == request.Owner, cancellationToken).ConfigureAwait(false))
        {
            throw new AccountRefusedException(AccountRefusal.PrimaryExists);
        }

        var key = AccountRules.NameKey(request.Name);
        if (await live.AnyAsync(a => a.NameKey == key, cancellationToken).ConfigureAwait(false))
        {
            throw new AccountRefusedException(AccountRefusal.NameInUse);
        }

        var targetKey = AccountRules.TargetIdKey(request.TargetId);
        if (await live.AnyAsync(a => a.TargetIdKey == targetKey, cancellationToken).ConfigureAwait(false))
        {
            throw new AccountRefusedException(AccountRefusal.TargetInUse);
        }
    }

    private async Task RequireNothingPendingAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        if (await db.NamingRequests.AnyAsync(r => r.NamingRuleId == ruleId && r.State == ProposedState, cancellationToken).ConfigureAwait(false))
        {
            throw new AccountRefusedException(AccountRefusal.AlreadyPending);
        }
    }

    private static SecondPersonState ParseState(string state) => state switch
    {
        nameof(SecondPersonState.Proposed) => SecondPersonState.Proposed,
        nameof(SecondPersonState.Approved) => SecondPersonState.Approved,
        nameof(SecondPersonState.Rejected) => SecondPersonState.Rejected,
        nameof(SecondPersonState.Withdrawn) => SecondPersonState.Withdrawn,
        _ => 0,
    };

    private static AccountLinkRequest Restore(AccountLinkRequestRow row) => AccountLinkRequest.Restore(
        row.Id,
        row.ApplicationId,
        Enum.GetNames<AccountType>().Contains(row.Type, StringComparer.Ordinal) ? Enum.Parse<AccountType>(row.Type) : 0,
        row.Name,
        row.TargetId,
        row.Owner,
        row.Maker,
        row.Reason,
        row.ProposedAtUtc,
        ParseState(row.State),
        row.Checker,
        row.DecisionReason,
        row.DecidedAtUtc);

    private static NamingRuleChangeRequest Restore(NamingRuleChangeRequestRow row) => NamingRuleChangeRequest.Restore(
        row.Id, row.NamingRuleId, row.VersionId, row.VersionNumber, row.ContentHash, row.Maker, row.Reason, row.ProposedAtUtc, ParseState(row.State), row.Checker, row.DecisionReason, row.DecidedAtUtc);

    private static void Apply(AccountLinkRequestRow row, AccountLinkRequest request)
    {
        row.State = request.State.ToString();
        row.Checker = request.Checker;
        row.DecisionReason = request.DecisionReason;
        row.DecidedAtUtc = request.DecidedAtUtc;
    }

    private static void Apply(NamingRuleChangeRequestRow row, NamingRuleChangeRequest request)
    {
        row.State = request.State.ToString();
        row.Checker = request.Checker;
        row.DecisionReason = request.DecisionReason;
        row.DecidedAtUtc = request.DecidedAtUtc;
    }

    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    /// <summary>Bumps the rule row's version: it serialises edits and proposals.</summary>
    private void Touch(NamingRule rule) => db.Entry(rule).Property(r => r.AccountType).IsModified = true;

    private Task RequireAsync(string action, CancellationToken cancellationToken) => RequirePersonAsync(Actor(), action, AccountRefusal.NotAllowed, cancellationToken);

    private async Task RequirePersonAsync(string person, string action, AccountRefusal refusal, CancellationToken cancellationToken)
    {
        if (!await permissions.CanAsync(person, action, cancellationToken).ConfigureAwait(false))
        {
            throw new AccountRefusedException(refusal);
        }
    }

    private string Actor() =>
        actorAccessor.CurrentActor is { Length: > 0 } actor ? actor : throw new AccountRefusedException(AccountRefusal.NotAllowed);

    private async Task<AccountLinkRequestRow> LoadLinkAsync(Guid id, CancellationToken cancellationToken) =>
        await db.LinkRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new AccountNotFoundException();

    private async Task<NamingRuleChangeRequestRow> LoadNamingAsync(Guid id, CancellationToken cancellationToken) =>
        await db.NamingRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new AccountNotFoundException();

    private static IQueryable<NamingRule> Current(IQueryable<NamingRule> rules) =>
        rules.Include(r => r.Versions.Where(v => v.State != NamingRuleVersionState.Superseded));

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex) || databaseProvider.IsConcurrencyFailure(ex))
        {
            throw new AccountConflictException("The account, request or naming rule was changed by someone else; reload it and try again.", ex);
        }
    }

    /// <summary>The refusal is evidence: audited as denied with its code, then committed.</summary>
    private async Task RefuseAsync(
        IDbContextTransaction tx, string action, string targetType, Guid? targetId, string step, AccountRefusedException refusal, AuditPayloadField? extra, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        List<AuditPayloadField> payload = [Field("step", step), Field("refusal", (refusal.Refusal ?? AccountRefusal.InvalidRecord).ToString())];
        if (extra is not null)
        {
            payload.Add(extra);
        }

        await audit.AppendAsync(tx.GetDbTransaction(), Event(action, targetType, targetId, AuditOutcome.Denied, null, [.. payload]), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuditEventRecord LinkRecord(string action, AccountLinkRequest request, string? reason, Guid? accountId)
    {
        List<AuditPayloadField> payload =
        [
            Field("applicationId", request.ApplicationId.ToString()),
            Field("type", request.Type.ToString()),
            Field("name", request.Name),
            Field("targetId", request.TargetId),
            Field("owner", request.Owner),
            Field("maker", request.Maker),
            Field("state", request.State.ToString()),
        ];
        if (accountId is { } id)
        {
            payload.Add(Field("accountId", id.ToString()));
            payload.Add(Field("verified", "false"));
        }

        return Event(action, AccountAuditActions.LinkTarget, request.Id, AuditOutcome.Succeeded, reason, [.. payload]);
    }

    private AuditEventRecord RuleRecord(string action, NamingRule rule, NamingRuleVersion version, NamingRuleChangeRequest? request, string? reason)
    {
        List<AuditPayloadField> payload =
        [
            Field("applicationId", rule.ApplicationId.ToString()),
            Field("accountType", rule.AccountType.ToString()),
            Field("versionNumber", version.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            Field("template", version.Template),
            Field("contentHash", version.ContentHash),
            Field("editedBy", version.EditedBy),
        ];
        if (request is not null)
        {
            payload.Add(Field("requestId", request.Id.ToString()));
            payload.Add(Field("maker", request.Maker));
            payload.Add(Field("state", request.State.ToString()));
        }

        return Event(action, AccountAuditActions.NamingTarget, rule.Id, AuditOutcome.Succeeded, reason, [.. payload]);
    }

    private AuditEventRecord Event(string action, string targetType, Guid? targetId, AuditOutcome outcome, string? reason, params AuditPayloadField[] payload) => new()
    {
        Action = action,
        TargetType = targetType,
        TargetId = targetId?.ToString(),
        Outcome = outcome,
        OccurredAtUtc = Now(),
        CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
        Reason = reason,
        Payload = payload,
    };

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    private static AccountView View(AccountEntity a) => new(
        a.Id, a.ApplicationId, a.Type.ToString(), a.Name, a.TargetId, a.Owner, a.Status.ToString(), a.Origin.ToString(), a.LinkRequestId, a.Verified);

    private static AccountLinkRequestView View(AccountLinkRequest r, Guid? accountId) => new(
        r.Id, r.ApplicationId, r.Type.ToString(), r.Name, r.TargetId, r.Owner, r.Maker, r.Reason, r.ProposedAtUtc, r.State.ToString(), r.Checker, r.DecisionReason, r.DecidedAtUtc, accountId);

    private static NamingRuleChangeRequestView View(NamingRuleChangeRequest r) => new(
        r.Id, r.NamingRuleId, r.VersionNumber, r.ContentHash, r.Maker, r.Reason, r.ProposedAtUtc, r.State.ToString(), r.Checker, r.DecisionReason, r.DecidedAtUtc);

    private NamingRuleView View(NamingRule rule) => new(
        rule.Id,
        rule.ApplicationId,
        rule.AccountType.ToString(),
        rule.CurrentActive is { } active ? View(active) : null,
        rule.CurrentDraft is { } draft ? View(draft) : null,
        db.Entry(rule).Property<long>(AuditColumns.Version).CurrentValue);

    private NamingRuleVersionView View(NamingRuleVersion v)
    {
        var template = v.ToTemplate();
        return new NamingRuleVersionView(
            v.Id,
            v.VersionNumber,
            v.State.ToString(),
            [.. template.Parts.Select(p => new NamePartView(p.Literal, p.Attribute, [.. p.Transforms.Select(t => t.ToString())], p.Length))],
            template.MaxLength,
            v.ContentHash,
            v.EditedBy,
            db.Entry(v).Property<long>(AuditColumns.Version).CurrentValue);
    }
}
