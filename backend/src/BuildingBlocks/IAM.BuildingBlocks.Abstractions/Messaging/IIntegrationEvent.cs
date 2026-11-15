namespace IAM.BuildingBlocks.Abstractions.Messaging;

/// <summary>
/// An event one module publishes for others through its outbox (ADR-0006, DL-076). Declared in the publishing module's
/// Contracts. The type is a dotted lower-case code, for example <c>workflow.approval.closed</c>; a change that an old
/// consumer can't read gets a new <see cref="SchemaVersion"/>. Payloads carry ids and codes only: never reasons, tokens,
/// secrets or attribute values.
/// </summary>
public interface IIntegrationEvent
{
    static abstract string MessageType { get; }

    static abstract int SchemaVersion { get; }
}
