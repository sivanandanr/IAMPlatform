using IAM.BuildingBlocks.Messaging;
using IAM.Modules.Account.Contracts;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Workflow.Contracts;

namespace IAM.Modules.Provisioning.Infrastructure.Items;

/// <summary>
/// Decides what an authorised item becomes (DL-093, DL-096), the same way when an event arrives and again before each run:
/// <list type="number">
/// <item>The approval's log must match the audit store (R-037).</item>
/// <item>It must be the item's approval, with the route, people and matrix evidence the item was frozen with: the route comes
/// from the frozen item, never a message, and a replayed route that differs is an integrity block.</item>
/// <item>It must authorise provisioning with no Action level open.</item>
/// <item>The entitlement version, the existing account, or the new account's name, target id and Primary rule must still hold
/// (checked before the target is touched), and a connector must serve the application.</item>
/// </list>
/// An approval that hasn't authorised anything, or an event that says more than the log, is not stored as a block: it may be
/// early or forged, and the real event must still be able to plan the item.
/// </summary>
/// <remarks>Since DL-099 the item takes the request item's correlation id, not the first event's.</remarks>
public sealed class ProvisioningDecider(
    IApprovalIntegrity integrity,
    IRequestItemSource items,
    IEntitlementDirectory entitlements,
    IAccountDirectory accounts,
    IAccountProvisioning accountRules,
    ConnectorBindings bindings)
{
    /// <summary>The plan or block for the item, or null when there is nothing to do now.</summary>
    public async Task<ProvisioningPlan?> DecideAsync(Guid itemId, int sequence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var report = await integrity.CheckAsync(itemId, cancellationToken).ConfigureAwait(false);
        switch (report.Status)
        {
            case ApprovalIntegrityStatus.Verified:
                break;
            case ApprovalIntegrityStatus.Changing:
                // The approval is still being written: try again later rather than block on a moving target.
                throw new ApprovalChangingException();
            default:
                return IntegrityBlock(itemId, report.Problem ?? report.Status.ToString(), now);
        }

        // A message that says more than the verified log, or an approval that hasn't authorised anything, plans nothing and
        // blocks nothing (DL-096): the genuine event can still come.
        if (report.RequestItemId != itemId || report.Actions < sequence || !report.ProvisioningAuthorized || report.ActionsOpen)
        {
            return null;
        }

        var actions = report.Actions;
        // The name is the one the last completed Action level recorded (DL-099), never one given after it.
        var approvedName = report.AccountName is { } recorded ? new ApprovedAccountName(recorded.AssignmentId, recorded.NameHash) : null;
        if (await items.FindAsync(itemId, approvedName, cancellationToken).ConfigureAwait(false) is not { } item
            || await items.FindSubmittedAsync(itemId, cancellationToken).ConfigureAwait(false) is not { } frozen)
        {
            return new(ProvisioningItem.Block(itemId, actions, BlockReason.ItemMissing, null, null, now), []);
        }

        var details = new ItemDetails(item.Beneficiary, item.ApplicationId, item.EntitlementId, item.EntitlementVersionId, item.AccountId, item.NewAccountType, item.AccountName);
        ProvisioningPlan Blocked(BlockReason reason) => Correlated(new(ProvisioningItem.Block(itemId, actions, reason, null, details, now), []), item);
        if (item.ItemId != itemId
            || !string.Equals(item.Beneficiary, report.Beneficiary, StringComparison.Ordinal)
            || !string.Equals(item.Requester, report.Requester, StringComparison.Ordinal))
        {
            return Blocked(BlockReason.ItemMismatch);
        }

        // The approval must be bound to exactly the route and matrix evidence the item was frozen with (DL-096).
        if (!SameRoute(report.Route, frozen))
        {
            return IntegrityBlock(itemId, "RouteMismatch", now);
        }

        // Only the version that was requested and approved, still active, under the same application.
        var entitlement = await entitlements.FindRequestableAsync(item.EntitlementId, cancellationToken).ConfigureAwait(false);
        if (entitlement is null || entitlement.VersionId != item.EntitlementVersionId || entitlement.ApplicationId != item.ApplicationId
            || !string.Equals(entitlement.ContentHash, item.EntitlementContentHash, StringComparison.Ordinal))
        {
            return Blocked(BlockReason.EntitlementChanged);
        }

        if (bindings.For(item.ApplicationId) is not { } binding)
        {
            return Blocked(BlockReason.ManualWork);
        }

        // The group must be in the application's allowed container (DL-098).
        if (!ProvisioningPlanner.IsInGroupContainer(entitlement.TechnicalName, binding))
        {
            return Blocked(BlockReason.EntitlementOutOfScope);
        }

        if (item.AccountId is { } accountId)
        {
            var account = await accounts.FindAsync(accountId, cancellationToken).ConfigureAwait(false);

            // As when it was requested (DL-091): active, the beneficiary's, a type the entitlement allows, and linked on the target.
            if (account is null || account.ApplicationId != item.ApplicationId || account.Status != "Active"
                || !string.Equals(account.Owner, item.Beneficiary, StringComparison.Ordinal)
                || !entitlement.AllowedAccountTypes.Contains(account.Type, StringComparer.Ordinal)
                || string.IsNullOrEmpty(account.TargetId))
            {
                return Blocked(BlockReason.AccountChanged);
            }

            return Correlated(ProvisioningPlanner.ForExistingAccount(itemId, actions, details, account.TargetId, entitlement.TechnicalName, binding, now), item);
        }

        if (item.AccountName is null || item.NewAccountType is null)
        {
            return approvedName is null ? Blocked(BlockReason.NoAccountName) : Blocked(BlockReason.NameNotApproved);
        }

        // The name must be exactly the one the approval recorded (DL-099).
        if (approvedName is not null && !string.Equals(AccountNameHash.Of(item.AccountName), approvedName.NameHash, StringComparison.Ordinal))
        {
            return Blocked(BlockReason.NameNotApproved);
        }

        // The account rules, before the target is touched (DL-096): the name and target id free, one live Primary.
        var refusal = await accountRules.CheckCreatableAsync(
            new CreatedAccount(itemId, item.ApplicationId, item.NewAccountType, item.AccountName, ProvisioningPlanner.NewAccountTarget(item.AccountName, binding), item.Beneficiary),
            cancellationToken).ConfigureAwait(false);
        return refusal switch
        {
            null => Correlated(ProvisioningPlanner.ForNewAccount(itemId, actions, details, entitlement.TechnicalName, binding, now), item),
            "NameInUse" => Blocked(BlockReason.NameInUse),
            _ => Blocked(BlockReason.AccountTaken),
        };
    }

    /// <summary>Whether the replayed approval is bound to the frozen item's matrix evidence and route, stage by stage.</summary>
    public static bool SameRoute(ApprovedRoute? approved, RequestItemSubmitted frozen)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        if (approved is null
            || approved.MatrixVersionId != frozen.MatrixVersionId
            || !string.Equals(approved.MatrixContentHash, frozen.MatrixContentHash, StringComparison.Ordinal)
            || approved.MatchedRuleId != frozen.MatchedRuleId
            || !string.Equals(approved.PolicyMaker, frozen.PolicyMaker, StringComparison.Ordinal)
            || approved.Stages.Count != frozen.Stages.Count)
        {
            return false;
        }

        var replayed = approved.Stages.OrderBy(s => s.Sequence).ThenBy(s => s.StageId).ToList();
        var asFrozen = frozen.Stages.OrderBy(s => s.Sequence).ThenBy(s => s.StageId).ToList();
        return replayed.Zip(asFrozen).All(pair => pair.First == new ApprovedStage(
            pair.Second.StageId,
            pair.Second.Sequence,
            pair.Second.Kind,
            pair.Second.Source,
            pair.Second.WorkgroupKey,
            pair.Second.ApplicationId,
            pair.Second.RoleTypeId,
            pair.Second.EntitlementId,
            pair.Second.Quorum,
            pair.Second.MinimumApprovals,
            pair.Second.Mandatory,
            pair.Second.Independent,
            pair.Second.DeadlineHours,
            pair.Second.OnTimeout));
    }

    /// <summary>The request item's correlation id on the planned or blocked item (DL-099), when it has a valid one.</summary>
    private static ProvisioningPlan Correlated(ProvisioningPlan plan, ProvisionableItem item)
    {
        if (plan.Item.CorrelationId is null && MessageCorrelation.IsTraceId(item.CorrelationId))
        {
            plan.Item.Correlate(item.CorrelationId!);
        }

        return plan;
    }

    private static ProvisioningPlan IntegrityBlock(Guid itemId, string problem, DateTimeOffset now) =>
        new(ProvisioningItem.Block(itemId, null, BlockReason.Integrity, ProvisioningItem.ProblemCode(problem, "Integrity"), null, now), []);
}

/// <summary>The approval kept changing while it was checked (DL-099): nothing was decided, so try again later without counting an attempt.</summary>
public sealed class ApprovalChangingException : InvalidOperationException
{
    public ApprovalChangingException()
        : base("The approval changed while it was checked; retry.")
    {
    }

    public ApprovalChangingException(string message)
        : base(message)
    {
    }

    public ApprovalChangingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
