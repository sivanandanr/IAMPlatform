using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Account.Contracts;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Identity.Contracts;
using IAM.Modules.Policy.Contracts;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Domain.Items;
using IAM.Modules.Request.Infrastructure.Persistence;
using IAM.Modules.Workflow.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Request.Infrastructure.Items;

/// <summary>Audit action codes and permissions of request items.</summary>
public static class RequestAuditActions
{
    public const string Submitted = "request.item.submitted";
    public const string Refused = "request.item.refused";
    public const string AccountNamed = "request.item.account_named";
    public const string Target = "request.item";

    public const string SubmitAction = AccessActions.RequestItemsSubmit;
    public const string ForOthersAction = AccessActions.RequestItemsForOthers;
    public const string ReadAction = AccessActions.RequestItemsRead;
    public const string NameAction = AccessActions.RequestItemsNameAccount;

    public static IReadOnlyList<string> All { get; } = [Submitted, Refused, AccountNamed];
}

/// <summary>
/// Request items (DL-091). Every rule is checked here, server-side, against each module's contract: the entitlement's active
/// version, the account and who it belongs to, "Access for", the route the active matrix selects for the facts, and that every
/// stage resolves. The item, its audit event and its outbox message are saved in one transaction; a refusal is audited as
/// denied with its code. Nothing is guessed: no route, no name and no matching account are refusals.
/// </summary>
public sealed class RequestItemService(
    RequestDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions,
    IEntitlementDirectory entitlements,
    IAccountDirectory accounts,
    IAccountNaming naming,
    IPersonAttributeSource people,
    IApprovalMatrix matrix,
    IApprovalStageChecks stages) : IRequestItems
{
    private const int MaxMine = 200;

    public async Task<RequestItemView> SubmitAsync(SubmitItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var requester = Actor();
        var now = UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        RequestItem frozen;
        string correlation;
        RequestItemSubmitted submitted;
        try
        {
            await RequireAsync(requester, RequestAuditActions.SubmitAction, RequestRefusal.NotAllowed, cancellationToken).ConfigureAwait(false);
            if (!SecondPersonText.IsPerson(item.Beneficiary))
            {
                throw new RequestRejectedException("Beneficiary", "must be one person's id");
            }

            if (!string.Equals(item.Beneficiary, requester, StringComparison.Ordinal))
            {
                await RequireAsync(requester, RequestAuditActions.ForOthersAction, RequestRefusal.NotForOthers, cancellationToken).ConfigureAwait(false);
            }

            var entitlement = await entitlements.FindRequestableAsync(item.EntitlementId, cancellationToken).ConfigureAwait(false)
                ?? throw new RequestRefusedException(RequestRefusal.EntitlementNotRequestable);
            var (accountType, newAccount) = await TargetAsync(item, requester, entitlement, cancellationToken).ConfigureAwait(false);
            var duration = RequestItem.Duration(entitlement.AccessForMode, entitlement.AccessForDays, entitlement.AccessForDefaultDays, entitlement.AccessForMaxDays, item.DurationDays);

            if (await db.Items.AnyAsync(i => i.Beneficiary == item.Beneficiary && i.EntitlementId == entitlement.EntitlementId && i.State == RequestItemState.Submitted, cancellationToken).ConfigureAwait(false))
            {
                throw new RequestRefusedException(RequestRefusal.AlreadyRequested);
            }

            var selection = await matrix.SelectAsync(await FactsAsync(item.Beneficiary, requester, entitlement, accountType, newAccount is not null, now, cancellationToken).ConfigureAwait(false), now, cancellationToken).ConfigureAwait(false);
            if (!selection.IsMatched || selection.MatchedRuleId is not { } ruleId || selection.VersionId is not { } versionId
                || selection.ContentHash is null || selection.EvidenceJson is null || !SecondPersonText.IsPerson(selection.PolicyMaker))
            {
                throw new RequestRefusedException(RequestRefusal.NoRoute);
            }

            var itemId = Guid.CreateVersion7();
            var stages = await ResolveRouteAsync(selection.Route, entitlement, cancellationToken).ConfigureAwait(false);

            // A new Primary or Secondary account needs a name: from the naming rule, or from a level that may give it (DL-091).
            if (newAccount is { Name: null } && !stages.Any(s => s.Kind == "Action" && s.Fields.Contains("AccountName")))
            {
                throw new RequestRefusedException(RequestRefusal.NoAccountName);
            }

            correlation = MessageCorrelation.Current();
            frozen = RequestItem.Freeze(
                itemId, requester, item.Beneficiary, entitlement.ApplicationId, entitlement.EntitlementId, entitlement.VersionId, entitlement.ContentHash,
                item.AccountId, newAccount, duration, versionId, selection.ContentHash, ruleId, selection.PolicyMaker!, selection.EvidenceJson,
                JsonSerializer.Serialize(stages, MessageEnvelopes.Json), now, correlation);
            submitted = new RequestItemSubmitted(
                itemId, requester, item.Beneficiary, entitlement.ApplicationId, entitlement.EntitlementId, versionId, selection.ContentHash, ruleId, selection.PolicyMaker!, stages);
        }
        catch (RequestRefusedException refusal)
        {
            await RefuseAsync(tx, item.EntitlementId, refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.Items.Add(frozen);
        db.Outbox.Add(OutboxMessageRow.From(MessageEnvelopes.Create(submitted, RequestEvents.Source, correlation, null, $"request.item.submitted:{frozen.Id:D}", now)));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            // The same access for the same person was submitted at the same moment. The failed statement may have ended this
            // transaction (PostgreSQL), so the refusal is audited in a new one.
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var refusal = new RequestRefusedException(RequestRefusal.AlreadyRequested, ex);
            await using var retry = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await RefuseAsync(retry, item.EntitlementId, refusal, cancellationToken).ConfigureAwait(false);
            throw refusal;
        }

        await audit.AppendAsync(tx.GetDbTransaction(), Record(frozen, correlation), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(frozen);
    }

    public async Task<RequestItemView?> FindAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        // People see their own items; anyone else needs the read permission, and a refusal looks like "not found".
        var own = string.Equals(item.Requester, actor, StringComparison.Ordinal) || string.Equals(item.Beneficiary, actor, StringComparison.Ordinal);
        return own || await permissions.CanAsync(actor, RequestAuditActions.ReadAction, cancellationToken).ConfigureAwait(false)
            ? View(item, await CurrentNameAsync(item, cancellationToken).ConfigureAwait(false))
            : null;
    }

    public async Task<IReadOnlyList<RequestItemView>> MineAsync(CancellationToken cancellationToken)
    {
        var actor = Actor();
        var items = await db.Items.AsNoTracking()
            .Where(i => i.Requester == actor || i.Beneficiary == actor)
            .OrderByDescending(i => i.SubmittedAtUtc)
            .Take(MaxMine)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = items.Select(i => i.Id).ToList();
        var names = (await db.AccountNames.AsNoTracking().Where(n => ids.Contains(n.ItemId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(n => n.ItemId)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Seq).Last().Name);
        return [.. items.Select(i => View(i, names.GetValueOrDefault(i.Id) ?? i.NewAccountName))];
    }

    /// <summary>
    /// A name for the item's new account, given on one of its open Action stages that lists the account-name field, by one of
    /// that stage's people (DL-092). Names only (rule 11): nothing else about the item can change, and the item stays frozen.
    /// </summary>
    public async Task<RequestItemView> GiveAccountNameAsync(Guid itemId, Guid stageId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var actor = Actor();
        var now = UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AccountNameAssignment given;
        try
        {
            await RequireAsync(actor, RequestAuditActions.NameAction, RequestRefusal.NotAllowed, cancellationToken).ConfigureAwait(false);

            // An item the caller isn't on a stage of looks the same whether or not it exists.
            if (item is null)
            {
                throw new RequestRefusedException(RequestRefusal.NotOnStage);
            }

            var route = JsonSerializer.Deserialize<List<SubmittedStage>>(item.Route, MessageEnvelopes.Json) ?? throw new RequestRefusedException(RequestRefusal.InvalidRecord);
            if (!route.Any(s => s.StageId == stageId && s.Kind == "Action" && s.Fields.Contains("AccountName", StringComparer.Ordinal)))
            {
                throw new RequestRefusedException(RequestRefusal.NotANameStage);
            }

            // The approval's instance id is the item id (DL-091).
            if (!await stages.CanActOnOpenActionStageAsync(item.Id, stageId, actor, cancellationToken).ConfigureAwait(false))
            {
                throw new RequestRefusedException(RequestRefusal.NotOnStage);
            }

            if (RequestItem.IsAccountName(name) && await accounts.IsNameInUseAsync(item.ApplicationId, name, cancellationToken).ConfigureAwait(false))
            {
                throw new RequestRefusedException(RequestRefusal.NameInUse);
            }

            given = AccountNameAssignment.Give(item, stageId, await CurrentNameAsync(item, cancellationToken).ConfigureAwait(false), name, actor, now);
        }
        catch (RequestRefusedException refusal)
        {
            await RefuseAsync(tx, item?.EntitlementId ?? Guid.Empty, refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.AccountNames.Add(given);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(
            tx.GetDbTransaction(),
            new AuditEventRecord
            {
                Action = RequestAuditActions.AccountNamed,
                TargetType = RequestAuditActions.Target,
                TargetId = item.Id.ToString(),
                Outcome = AuditOutcome.Succeeded,
                OccurredAtUtc = now,
                CorrelationId = item.CorrelationId ?? MessageCorrelation.Current(),
                CausationId = item.CorrelationId is null ? null : MessageCorrelation.Current(),
                Payload =
                [
                    Field("stageId", stageId.ToString()),
                    Field("newAccountType", item.NewAccountType ?? "none"),
                    Field("proposedNameHash", Hash(item.NewAccountName)),
                    Field("previousNameHash", Hash(given.PreviousName)),
                    Field("nameHash", Hash(given.Name)),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(item, given.Name);
    }

    /// <summary>The account name now: the latest assignment, else the naming rule's proposal (or the owner's, for Service and Shared).</summary>
    private async Task<string?> CurrentNameAsync(RequestItem item, CancellationToken cancellationToken) =>
        await db.AccountNames.AsNoTracking()
            .Where(n => n.ItemId == item.Id)
            .OrderByDescending(n => n.Seq)
            .Select(n => n.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
        ?? item.NewAccountName;

    /// <summary>
    /// Checks the account the item names, or the new one it asks for (spec v3.2 §2.5, §3.1, §5.3). Returns the account type and,
    /// for a new account, what to create.
    /// </summary>
    private async Task<(string AccountType, NewAccount? NewAccount)> TargetAsync(SubmitItem item, string requester, RequestableEntitlement entitlement, CancellationToken cancellationToken)
    {
        if ((item.AccountId is null) == (item.NewAccountType is null))
        {
            throw new RequestRejectedException("Account", "must name either an existing account or a new account's type");
        }

        if (item.AccountId is { } accountId)
        {
            if (item.NewAccountName is not null)
            {
                throw new RequestRejectedException("NewAccountName", "is only for a new account");
            }

            var account = await accounts.FindAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (account is null || account.Status != "Active" || account.ApplicationId != entitlement.ApplicationId)
            {
                throw new RequestRefusedException(RequestRefusal.AccountNotUsable);
            }

            if (!string.Equals(account.Owner, item.Beneficiary, StringComparison.Ordinal))
            {
                throw new RequestRefusedException(RequestRefusal.AccountNotBeneficiarys);
            }

            RequireTypeAllowed(account.Type, entitlement, requester, item.Beneficiary);
            return (account.Type, null);
        }

        var type = item.NewAccountType!;
        if (!RequestItem.IsAccountType(type))
        {
            throw new RequestRejectedException("NewAccountType", "must be Primary, Secondary, Service or Shared");
        }

        RequireTypeAllowed(type, entitlement, requester, item.Beneficiary);
        if (type == "Primary"
            && (await accounts.ForOwnerAsync(entitlement.ApplicationId, item.Beneficiary, cancellationToken).ConfigureAwait(false)).Any(a => a.Type == "Primary"))
        {
            throw new RequestRefusedException(RequestRefusal.PrimaryExists);
        }

        if (type is "Service" or "Shared")
        {
            // The owner names a Service or Shared account; a name is not access (rule 11).
            if (!RequestItem.IsAccountName(item.NewAccountName))
            {
                throw new RequestRejectedException("NewAccountName", "is required for a Service or Shared account: letters, digits, dots, underscores or hyphens");
            }

            return (type, new NewAccount(type, item.NewAccountName));
        }

        if (item.NewAccountName is not null)
        {
            throw new RequestRejectedException("NewAccountName", "comes from the naming rule or an approval level for a Primary or Secondary account");
        }

        // The naming rule proposes it from the person's attributes; with none (Phase 0) it fails closed and a level gives it.
        var attributes = (await people.FindAsync(item.Beneficiary, cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        var proposed = await naming.ProposeAsync(entitlement.ApplicationId, type, attributes, cancellationToken).ConfigureAwait(false);
        return (type, new NewAccount(type, proposed.Name));
    }

    /// <summary>The entitlement must allow the type; privileged never on Primary; only the owner requests for a Service or Shared account.</summary>
    private static void RequireTypeAllowed(string type, RequestableEntitlement entitlement, string requester, string beneficiary)
    {
        if (entitlement.Privileged && type == "Primary")
        {
            throw new RequestRefusedException(RequestRefusal.PrivilegedOnPrimary);
        }

        if (!entitlement.AllowedAccountTypes.Contains(type, StringComparer.Ordinal))
        {
            throw new RequestRefusedException(RequestRefusal.AccountTypeNotAllowed);
        }

        if (type is "Service" or "Shared" && !(string.Equals(requester, beneficiary, StringComparison.Ordinal)))
        {
            throw new RequestRefusedException(RequestRefusal.NotAccountOwner);
        }
    }

    /// <summary>The built-in facts (DL-090f), plus the people's attributes from the source (none in Phase 0).</summary>
    private async Task<List<MatrixFact>> FactsAsync(string beneficiary, string requester, RequestableEntitlement entitlement, string accountType, bool newAccount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<MatrixFact> facts =
        [
            new("Request", BuiltInFactIds.RequestAction, "Grant", now),
            new("Request", BuiltInFactIds.RequestAccountType, accountType, now),
            new("Request", BuiltInFactIds.RequestNewAccount, newAccount ? "true" : "false", now),
            new("Entitlement", BuiltInFactIds.EntitlementKey, entitlement.Key, now),
            new("Entitlement", BuiltInFactIds.EntitlementPrivileged, entitlement.Privileged ? "true" : "false", now),
            new("Entitlement", BuiltInFactIds.EntitlementRisk, entitlement.Risk, now),
            new("Entitlement", BuiltInFactIds.EntitlementType, entitlement.Type, now),
            new("Application", BuiltInFactIds.ApplicationKey, entitlement.ApplicationKey, now),
        ];
        facts.AddRange((await people.FindAsync(beneficiary, cancellationToken).ConfigureAwait(false)).Select(a => new MatrixFact("Beneficiary", a.AttributeId, a.Value, a.SynchronizedAtUtc)));
        facts.AddRange((await people.FindAsync(requester, cancellationToken).ConfigureAwait(false)).Select(a => new MatrixFact("Requester", a.AttributeId, a.Value, a.SynchronizedAtUtc)));
        return facts;
    }

    /// <summary>Every stage's source resolved to ids now, so what Workflow receives can't be read differently later.</summary>
    private async Task<IReadOnlyList<SubmittedStage>> ResolveRouteAsync(IReadOnlyList<MatrixRouteStage> route, RequestableEntitlement entitlement, CancellationToken cancellationToken)
    {
        if (route.Count is 0 or > RequestItemSubmitted.MaxStages)
        {
            throw new RequestRefusedException(RequestRefusal.NoRoute);
        }

        var stages = new List<SubmittedStage>(route.Count);
        foreach (var stage in route)
        {
            var (workgroup, application, roleType, entitlementId) = stage.Source switch
            {
                "EntitlementOwner" => ((string?)null, (Guid?)null, (Guid?)null, (Guid?)entitlement.EntitlementId),
                "ApplicationOwner" => (null, entitlement.ApplicationId, null, null),
                "ApplicationRole" => (null, entitlement.ApplicationId,
                    await entitlements.FindRoleTypeIdAsync(stage.RoleTypeCode ?? string.Empty, cancellationToken).ConfigureAwait(false)
                        ?? throw new RequestRefusedException(RequestRefusal.RouteNotResolvable), null),
                "Workgroup" when stage.WorkgroupKey is not null => (stage.WorkgroupKey, null, null, null),
                _ => throw new RequestRefusedException(RequestRefusal.RouteNotResolvable),
            };
            stages.Add(new SubmittedStage(
                Guid.CreateVersion7(), stage.Sequence, stage.Kind, stage.Source, workgroup, application, roleType, entitlementId,
                stage.Quorum, stage.MinimumApprovals, stage.Mandatory, stage.Independent, stage.DeadlineHours, stage.OnTimeout, stage.Fields));
        }

        return stages;
    }

    private async Task RequireAsync(string person, string action, RequestRefusal refusal, CancellationToken cancellationToken)
    {
        if (!await permissions.CanAsync(person, action, cancellationToken).ConfigureAwait(false))
        {
            throw new RequestRefusedException(refusal);
        }
    }

    private string Actor() =>
        actorAccessor.CurrentActor is { Length: > 0 } actor ? actor : throw new RequestRefusedException(RequestRefusal.NotAllowed);

    private async Task RefuseAsync(IDbContextTransaction tx, Guid entitlementId, RequestRefusedException refusal, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await audit.AppendAsync(
            tx.GetDbTransaction(),
            new AuditEventRecord
            {
                Action = RequestAuditActions.Refused,
                TargetType = RequestAuditActions.Target,
                TargetId = null,
                Outcome = AuditOutcome.Denied,
                OccurredAtUtc = UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow()),
                CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
                Payload = [Field("entitlementId", entitlementId.ToString()), Field("refusal", (refusal.Refusal ?? RequestRefusal.InvalidRecord).ToString())],
            },
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What was asked and why this route: the audit trail alone shows the item, the matrix version and rule, and the route.</summary>
    private static AuditEventRecord Record(RequestItem item, string correlation) => new()
    {
        Action = RequestAuditActions.Submitted,
        TargetType = RequestAuditActions.Target,
        TargetId = item.Id.ToString(),
        Outcome = AuditOutcome.Succeeded,
        OccurredAtUtc = item.SubmittedAtUtc,
        CorrelationId = correlation,
        Payload =
        [
            Field("beneficiary", item.Beneficiary),
            Field("entitlementId", item.EntitlementId.ToString()),
            Field("entitlementVersionId", item.EntitlementVersionId.ToString()),
            Field("entitlementContentHash", item.EntitlementContentHash),
            Field("accountId", item.AccountId?.ToString() ?? "none"),
            Field("newAccountType", item.NewAccountType ?? "none"),
            Field("newAccountName", item.NewAccountName ?? "none"),
            Field("durationDays", item.DurationDays?.ToString(CultureInfo.InvariantCulture) ?? "none"),
            Field("matrixVersionId", item.MatrixVersionId.ToString()),
            Field("matrixContentHash", item.MatrixContentHash),
            Field("matchedRuleId", item.MatchedRuleId.ToString()),
            Field("route", item.Route),
        ],
    };

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    /// <summary>A name in the audit as its hash (DL-100), as Workflow records it; never the login itself.</summary>
    private static string Hash(string? name) => name is null ? "none" : AccountNameHash.Of(name);

    private static RequestItemView View(RequestItem i, string? accountName = null) => new(
        i.Id, i.Requester, i.Beneficiary, i.ApplicationId, i.EntitlementId, i.EntitlementVersionId, i.AccountId, i.NewAccountType, i.NewAccountName,
        i.DurationDays, i.State.ToString(), i.MatrixVersionId, i.MatchedRuleId, i.SubmittedAtUtc, accountName ?? i.NewAccountName);
}
