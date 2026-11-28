namespace IAM.Modules.Provisioning.Application.Items;

/// <summary>One planned step as a target system runs it (DL-094): the operation id, the action and the objects. Ids and codes only.</summary>
public sealed record TargetStep(
    Guid OperationId,
    string Action,
    string TargetType,
    string TargetId,
    string? RelatedType,
    string? RelatedId,
    IReadOnlyDictionary<string, string?> Attributes)
{
    /// <summary>The id and action only: targets can name people.</summary>
    public override string ToString() => $"{nameof(TargetStep)} {OperationId} {Action}";
}

/// <summary>How a step ended on the target (DL-094).</summary>
public enum StepStatus
{
    /// <summary>The change was made, or the target was already as asked.</summary>
    Done = 1,

    /// <summary>It failed for now (unavailable, throttled): run it again later under the same operation id.</summary>
    Retry = 2,

    /// <summary>It can never succeed as given (rejected, unsupported): a person must look at it.</summary>
    Rejected = 3,

    /// <summary>The outcome is unknown (the call broke off): the target must be read before anything else.</summary>
    Unknown = 4,
}

/// <summary>A step's outcome: a status and the target's short code, never message text.</summary>
public sealed record StepResult(StepStatus Status, string? Code);

/// <summary>Step codes a target gives that the runner acts on itself.</summary>
public static class StepCodes
{
    /// <summary>
    /// With <see cref="StepStatus.Unknown"/>: the target's stored record of this step can't be trusted (DL-106, DL-107). Whether
    /// the change was made is unknown and no retry or read settles it: the item stops for a person.
    /// </summary>
    public const string LedgerDamaged = "LedgerDamaged";
}

/// <summary>
/// A target system the runner can change (DL-094). The provisioning worker adapts each connector in the reviewed allowlist to
/// this, so the module never depends on the connector SDK (ADR-0007).
/// </summary>
public interface IProvisioningTarget
{
    Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the target now matches what the operation asked for, by reading it; null if it couldn't be read (DL-100): a read
    /// that fails proves nothing either way.
    /// </summary>
    Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken);

    /// <summary>Whether an object is there on the target now (DL-098); null if it couldn't be read (DL-100).</summary>
    Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken);
}

/// <summary>The targets by connector key; null for a key that isn't configured.</summary>
public interface IProvisioningTargets
{
    IProvisioningTarget? Find(string connectorKey);
}

/// <summary>The runner's settings (DL-094), all bounded.</summary>
public sealed record ProvisioningRunnerSettings
{
    public ProvisioningRunnerSettings(int? intervalSeconds = null, int? batchSize = null, int? leaseSeconds = null, int? maxAttempts = null, int? retryDelaySeconds = null, int? stepTimeoutSeconds = null, int? undoMaxAttempts = null, int? maxPostponements = null)
    {
        Interval = TimeSpan.FromSeconds(Bounded(intervalSeconds ?? 10, 1, 3600, nameof(intervalSeconds)));
        BatchSize = Bounded(batchSize ?? 20, 1, 500, nameof(batchSize));
        Lease = TimeSpan.FromSeconds(Bounded(leaseSeconds ?? 300, 30, 3600, nameof(leaseSeconds)));
        MaxAttempts = Bounded(maxAttempts ?? 5, 1, 50, nameof(maxAttempts));
        RetryDelay = TimeSpan.FromSeconds(Bounded(retryDelaySeconds ?? 30, 1, 3600, nameof(retryDelaySeconds)));

        // A call to the target never outlasts half the lease; the lease is renewed before every call (DL-100).
        StepTimeout = TimeSpan.FromSeconds(Bounded(stepTimeoutSeconds ?? Math.Min(60, (int)Lease.TotalSeconds / 2), 1, (int)Lease.TotalSeconds / 2, nameof(stepTimeoutSeconds)));
        UndoMaxAttempts = Bounded(undoMaxAttempts ?? 10, 1, 100, nameof(undoMaxAttempts));
        MaxPostponements = Bounded(maxPostponements ?? 20, 1, 1000, nameof(maxPostponements));
    }

    /// <summary>Undo passes allowed before an item fails UndoFailed (DL-100).</summary>
    public int UndoMaxAttempts { get; }

    /// <summary>Runs put off for an approval still being written before each further one counts an attempt (DL-100).</summary>
    public int MaxPostponements { get; }

    public const string Section = "Provisioning:Runner";

    public TimeSpan Interval { get; }

    public int BatchSize { get; }

    public TimeSpan Lease { get; }

    /// <summary>Temporary failures allowed before the item fails for a person to look at.</summary>
    public int MaxAttempts { get; }

    /// <summary>The longest one call to the target may take; a call that takes longer has an unknown outcome (DL-097).</summary>
    public TimeSpan StepTimeout { get; }

    /// <summary>The first wait after a temporary failure; it doubles each time, up to an hour.</summary>
    public TimeSpan RetryDelay { get; }

    public TimeSpan DelayAfter(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, RetryDelay.TotalSeconds * Math.Pow(2, Math.Clamp(attempts - 1, 0, 20))));

    private static int Bounded(int value, int min, int max, string name) =>
        value >= min && value <= max ? value : throw new ArgumentOutOfRangeException(name, $"{name} must be {min} to {max}.");
}
