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

    public async Task<bool> VerifyAsync(TargetStep operation, CancellationToken cancellationToken) =>
        (await connector.VerifyAsync(ToOperation(operation), cancellationToken).ConfigureAwait(false)).Matches;

    public async Task<bool> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) =>
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
