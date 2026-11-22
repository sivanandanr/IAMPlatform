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
    public async Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken)
    {
        var result = await connector.ExecuteAsync(ToOperation(operation), cancellationToken).ConfigureAwait(false);
        return new StepResult(
            result.Status switch
            {
                OperationStatus.Applied or OperationStatus.AlreadyInState => StepStatus.Done,
                OperationStatus.Failed => StepStatus.Retry,
                _ => StepStatus.Rejected,
            },
            result.Code ?? result.Status.ToString());
    }

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
