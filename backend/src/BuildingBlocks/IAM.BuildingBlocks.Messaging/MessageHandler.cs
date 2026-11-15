namespace IAM.BuildingBlocks.Messaging;

/// <summary>What happened to a message a consumer received (DL-078).</summary>
public enum MessageOutcome
{
    /// <summary>Its effects were saved, together with its inbox row.</summary>
    Processed = 1,

    /// <summary>This consumer had already processed it: nothing was done again.</summary>
    Duplicate = 2,
}

/// <summary>
/// One consumer of another module's events (ADR-0006, DL-078). It names the module it listens to and the event types it
/// takes; anything else is refused before it gets here. It records the message in its inbox in the same transaction as
/// its effects, so a message delivered twice is processed once. It throws <see cref="InvalidMessageException"/> for a
/// message it can never process (sent to the dead-letter queue at once) and anything else for a failure worth retrying.
/// </summary>
public interface IMessageHandler
{
    /// <summary>A dotted lower-case name, unique per consumer, for example <c>notification.approval_tasks</c>. Also its queue.</summary>
    string ConsumerName { get; }

    /// <summary>The publishing module, for example <c>workflow</c>.</summary>
    string Source { get; }

    IReadOnlyList<string> MessageTypes { get; }

    Task<MessageOutcome> HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken);
}
