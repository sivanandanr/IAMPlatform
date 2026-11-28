using IAM.Connectors.Sdk;
using IAM.Modules.Provisioning.Application.Items;

namespace IAM.Workers.Provisioning;

/// <summary>
/// A connector as a provisioning target (DL-094): the planned step becomes a connector operation under its fixed operation id,
/// and the connector's outcome becomes a step status. Applied and already-in-state are done; failed is temporary; rejected
/// and unsupported need a person. The connector's code is passed on; its messages never are.
/// </summary>
public sealed class ConnectorTarget(IConnector connector) : IProvisioningTarget
{
    /// <summary>
    /// Whether the connector runs on the SDK's pipeline (DL-105): only then is a before-write code the SDK's own word. A connector
    /// written straight on <see cref="IConnector"/> (a proxy to a remote agent, say) could pass one on after a write, and so could
    /// one built on <see cref="ConnectorBase"/> that implements <see cref="IConnector.ExecuteAsync"/> again itself (DL-106).
    /// </summary>
    private readonly bool _sdk = RunsSdkPipeline(connector.GetType());

    /// <summary>
    /// Whether a connector type's writes go through <see cref="ConnectorBase"/>'s own pipeline (DL-106): it derives from it, and
    /// its <see cref="IConnector.ExecuteAsync"/> is still the SDK's, not one it implements again.
    /// </summary>
    public static bool RunsSdkPipeline(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(ConnectorBase).IsAssignableFrom(type))
        {
            return false;
        }

        var map = type.GetInterfaceMap(typeof(IConnector));
        var execute = Array.IndexOf(map.InterfaceMethods, typeof(IConnector).GetMethod(nameof(IConnector.ExecuteAsync)));
        return execute >= 0 && map.TargetMethods[execute].DeclaringType == typeof(ConnectorBase);
    }

    public async Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken)
    {
        OperationResult result;
        try
        {
            result = await connector.ExecuteAsync(ToOperation(operation), cancellationToken).ConfigureAwait(false);
        }
        catch (LedgerRecordDamagedException) when (_sdk)
        {
            // The connector's record of this operation can't be trusted (DL-107): only this, never any other fault, is told
            // apart, and the runner stops the item for a person. Only the SDK's own refusal counts (DL-108): from a connector not
            // on the SDK the exception is a call that broke off, like anything else thrown.
            return new StepResult(StepStatus.Unknown, StepCodes.LedgerDamaged);
        }

        // The damage code is this target's alone (DL-108): a connector that reports it itself is a connector fault, never damage.
        // With a success it contradicts itself, so whether the target changed is unknown (DL-109), as the SDK takes a success
        // that claims its speed limit.
        if (result.Code == StepCodes.LedgerDamaged)
        {
            result = result.Status is OperationStatus.Applied or OperationStatus.AlreadyInState
                ? result with { Status = OperationStatus.Failed, Code = ResultCodes.InvalidResponse }
                : result with { Code = ResultCodes.ConnectorError };
        }

        // "Already as asked" always says so in its code (DL-109): the runner's guard that a create never takes over an object
        // that was there matches the code, so no connector code may hide it.
        if (result.Status == OperationStatus.AlreadyInState)
        {
            result = result with { Code = nameof(OperationStatus.AlreadyInState) };
        }

        return new StepResult(
            result.Status switch
            {
                OperationStatus.Applied or OperationStatus.AlreadyInState => StepStatus.Done,

                // A failed write may have landed before the fault (a dropped connection, a time-out, a garbled answer), and the
                // connector's ledger keeps no failure: whether the target changed is unknown (DL-102, DL-103), never "not done".
                // Only the SDK's speed limit is known to come before any write (DL-104), and only from the SDK (DL-105).
                OperationStatus.Failed when _sdk && BeforeWriteCodes.Contains(result.Code ?? string.Empty) => StepStatus.Retry,
                OperationStatus.Failed => StepStatus.Unknown,
                _ => StepStatus.Rejected,
            },
            result.Code ?? result.Status.ToString())
        {
            // Only the SDK's word (DL-109): a connector not on it can't say it answered from a ledger.
            Replayed = _sdk && result.Replayed,
        };
    }

    /// <summary>
    /// The connector codes of a failed write known to come before anything was written (DL-103): the SDK's rate limiter refuses
    /// before the connector is called, with a code no connector can report (DL-104). Every other failed write, a connector's own
    /// <c>Throttled</c> included, may have changed the target.
    /// </summary>
    public static readonly IReadOnlySet<string> BeforeWriteCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ResultCodes.RateLimited,
    };

    /// <summary>
    /// The connector reports a read it couldn't make as "no match" with a code (DL-100): a failure worth retrying proves nothing,
    /// so it is null here. A read the connector refuses outright (the step itself is invalid) never succeeds later, so it is no
    /// match, and fails at once rather than after every retry (DL-101). Only a clean read answers yes.
    /// </summary>
    public async Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken)
    {
        var result = await connector.VerifyAsync(ToOperation(operation), cancellationToken).ConfigureAwait(false);
        return result.Code switch
        {
            null => result.Matches,
            { } code when RetryableReadCodes.Contains(code) => null,
            _ => false,
        };
    }

    /// <summary>The connector codes of a read that failed for now (its transient faults), as against one it refused.</summary>
    public static readonly IReadOnlySet<string> RetryableReadCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ResultCodes.ConnectorError, ResultCodes.InvalidResponse, ResultCodes.TargetUnavailable, ResultCodes.Throttled, ResultCodes.TimedOut,

        // The SDK never answers a read with it (DL-105), but a connector not on the SDK may: busy, not a mismatch (DL-106).
        ResultCodes.RateLimited,
    };

    /// <summary>
    /// Whether the object is there. A read that fails for now throws (the runner takes it as unknown); a reference the connector
    /// can't read at all is "not there", which fails closed (AccountNotOnTarget) before anything is granted (DL-101).
    /// </summary>
    public async Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) =>
        await connector.ReadAsync(new ObjectRef(objectType, id), cancellationToken).ConfigureAwait(false) is not null;

    public static ConnectorOperation ToOperation(TargetStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new ConnectorOperation(
            step.OperationId,
            step.Action,
            new ObjectRef(step.TargetType, step.TargetId),
            step.RelatedType is null || step.RelatedId is null ? null : new ObjectRef(step.RelatedType, step.RelatedId),
            step.Attributes,
            null);
    }
}

/// <summary>The configured targets by connector key (DL-094).</summary>
public sealed class ConnectorTargets(IReadOnlyDictionary<string, IProvisioningTarget> targets) : IProvisioningTargets
{
    public IProvisioningTarget? Find(string connectorKey) => targets.GetValueOrDefault(connectorKey);
}
