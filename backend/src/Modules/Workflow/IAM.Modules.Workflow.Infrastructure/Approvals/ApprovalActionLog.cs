using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Workflow.Domain.Approvals;

namespace IAM.Modules.Workflow.Infrastructure.Approvals;

/// <summary>What an accepted action was. Stored as text; never renumber or rename a published one.</summary>
public enum ApprovalActionKind
{
    Bound = 1,
    ApproversSupplied = 2,
    Decided = 3,
    Completed = 4,
    Returned = 5,
    Withdrawn = 6,
    Cancelled = 7,
    Escalated = 8,
    EscalationProposed = 9,
    EscalationApproved = 10,
    EscalationWithdrawn = 11,
    TimeoutsApplied = 12,
}

/// <summary>One accepted action: its kind, its inputs as JSON, when it happened and who acted.</summary>
public sealed record LoggedAction(ApprovalActionKind Kind, string Payload, DateTimeOffset AtUtc, string? Actor)
{
    /// <summary>The kind only: logging this object must not expose a person, reason or route.</summary>
    public override string ToString() => $"{nameof(LoggedAction)} {Kind}";
}

/// <summary>
/// The action log (DL-072a). The live path and the replay run every action through <see cref="Apply"/> from the same
/// JSON, so a stored instance is rebuilt by exactly the code that built it. The engine is deterministic, so the replay must
/// reproduce the stored history; anything else is damage.
/// </summary>
public static class ApprovalActionLog
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new PersonIdJsonConverter(), new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static LoggedAction Bound(ApprovalBinding binding, ApprovalRoute route, DateTimeOffset at) =>
        Log(ApprovalActionKind.Bound, new BoundPayload(binding, route.Stages), at, null);

    public static LoggedAction Supplied(Guid stageId, ApproverResolution resolution, DateTimeOffset at) =>
        Log(ApprovalActionKind.ApproversSupplied, new SuppliedPayload(stageId, resolution), at, null);

    public static LoggedAction Decided(ApprovalDecision decision, DateTimeOffset at) =>
        Log(ApprovalActionKind.Decided, decision, at, decision.Actor);

    public static LoggedAction Completed(ActionCompletion completion, DateTimeOffset at) =>
        Log(ApprovalActionKind.Completed, completion, at, completion.Actor);

    public static LoggedAction Returned(Guid stageId, PersonId actor, PersonId? onBehalfOf, string? reason, long expectedVersion, DateTimeOffset at) =>
        Log(ApprovalActionKind.Returned, new ReturnedPayload(stageId, actor, onBehalfOf, reason, expectedVersion), at, actor);

    public static LoggedAction Withdrawn(PersonId actor, string? reason, long expectedVersion, DateTimeOffset at) =>
        Log(ApprovalActionKind.Withdrawn, new ClosedPayload(actor, reason, expectedVersion), at, actor);

    public static LoggedAction Cancelled(PersonId actor, string? reason, long expectedVersion, DateTimeOffset at) =>
        Log(ApprovalActionKind.Cancelled, new ClosedPayload(actor, reason, expectedVersion), at, actor);

    public static LoggedAction Escalated(Guid stageId, IReadOnlyList<PersonId> candidates, PersonId actor, string? reason, long expectedVersion, DateTimeOffset at) =>
        Log(ApprovalActionKind.Escalated, new EscalationPayload(stageId, candidates, actor, reason, expectedVersion), at, actor);

    public static LoggedAction Proposed(Guid stageId, IReadOnlyList<PersonId> candidates, PersonId actor, string? reason, long expectedVersion, DateTimeOffset at) =>
        Log(ApprovalActionKind.EscalationProposed, new EscalationPayload(stageId, candidates, actor, reason, expectedVersion), at, actor);

    public static LoggedAction EscalationApproved(Guid stageId, IReadOnlyList<PersonId> reviewed, PersonId approver, string? reason, long expectedVersion, DateTimeOffset at) =>
        Log(ApprovalActionKind.EscalationApproved, new EscalationPayload(stageId, reviewed, approver, reason, expectedVersion), at, approver);

    public static LoggedAction EscalationWithdrawn(Guid stageId, PersonId actor, string? reason, long expectedVersion, DateTimeOffset at) =>
        Log(ApprovalActionKind.EscalationWithdrawn, new EscalationPayload(stageId, [], actor, reason, expectedVersion), at, actor);

    public static LoggedAction TimeoutsApplied(long expectedVersion, DateTimeOffset at) =>
        Log(ApprovalActionKind.TimeoutsApplied, new TimeoutsPayload(expectedVersion), at, null);

    /// <summary>The completion a <see cref="ApprovalActionKind.Completed"/> action recorded, else null.</summary>
    public static ActionCompletion? CompletionOf(LoggedAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind == ApprovalActionKind.Completed ? Read<ActionCompletion>(action.Payload) : null;
    }

    /// <summary>Starts an instance from a <see cref="ApprovalActionKind.Bound"/> action.</summary>
    public static ApprovalInstance Start(LoggedAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Kind != ApprovalActionKind.Bound)
        {
            throw new InvalidDataException("The first action must bind the instance.");
        }

        RequireActor(action, null);
        var bound = Read<BoundPayload>(action.Payload);
        return ApprovalInstance.Bind(bound.Binding, ApprovalRoute.Create(bound.Stages), action.AtUtc);
    }

    /// <summary>The stages a <see cref="ApprovalActionKind.Bound"/> action set out, as validated when it was stored.</summary>
    public static IReadOnlyList<StageDefinition> StagesOf(LoggedAction bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        return Read<BoundPayload>(bound.Payload).Stages;
    }

    /// <summary>Applies one later action. Refusals come from the engine, exactly as for a live call.</summary>
    public static void Apply(ApprovalInstance instance, LoggedAction action)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(action);
        var at = action.AtUtc;
        switch (action.Kind)
        {
            case ApprovalActionKind.ApproversSupplied:
                {
                    var p = Read<SuppliedPayload>(action.Payload);
                    RequireActor(action, null);
                    instance.SupplyApprovers(p.StageId, p.Resolution, instance.Version, at);
                    break;
                }

            case ApprovalActionKind.Decided:
                {
                    var p = Read<ApprovalDecision>(action.Payload);
                    RequireActor(action, p.Actor);
                    instance.Decide(p, at);
                    break;
                }

            case ApprovalActionKind.Completed:
                {
                    var p = Read<ActionCompletion>(action.Payload);
                    RequireActor(action, p.Actor);
                    if (!p.NameIsWellFormed)
                    {
                        throw new InvalidDataException("A completion's recorded account name is malformed.");
                    }

                    instance.CompleteAction(p, at);
                    break;
                }

            case ApprovalActionKind.Returned:
                {
                    var p = Read<ReturnedPayload>(action.Payload);
                    RequireActor(action, p.Actor);
                    instance.ReturnForChanges(p.StageId, p.Actor, p.OnBehalfOf, p.Reason, p.ExpectedVersion, at);
                    break;
                }

            case ApprovalActionKind.Withdrawn:
                {
                    var p = Read<ClosedPayload>(action.Payload);
                    RequireActor(action, p.Actor);
                    instance.Withdraw(p.Actor, p.Reason, p.ExpectedVersion, at);
                    break;
                }

            case ApprovalActionKind.Cancelled:
                {
                    var p = Read<ClosedPayload>(action.Payload);
                    RequireActor(action, p.Actor);
                    instance.Cancel(p.Actor, p.Reason, p.ExpectedVersion, at);
                    break;
                }

            case ApprovalActionKind.Escalated:
                {
                    var p = Read<EscalationPayload>(action.Payload);
                    RequireActor(action, p.Actor);
                    instance.Escalate(p.StageId, p.Candidates, p.Actor, p.Reason, p.ExpectedVersion, at);
                    break;
                }

            case ApprovalActionKind.EscalationProposed:
                {
                    var p = Read<EscalationPayload>(action.Payload);
                    RequireActor(action, p.Actor);
                    instance.ProposeEscalation(p.StageId, p.Candidates, p.Actor, p.Reason, p.ExpectedVersion, at);
                    break;
                }

            case ApprovalActionKind.EscalationApproved:
                {
                    var p = Read<EscalationPayload>(action.Payload);
                    RequireActor(action, p.Actor);
                    instance.ApproveEscalation(p.StageId, p.Candidates, p.Actor, p.Reason, p.ExpectedVersion, at);
                    break;
                }

            case ApprovalActionKind.EscalationWithdrawn:
                {
                    var p = Read<EscalationPayload>(action.Payload);
                    RequireActor(action, p.Actor);
                    instance.WithdrawEscalation(p.StageId, p.Actor, p.Reason, p.ExpectedVersion, at);
                    break;
                }

            case ApprovalActionKind.TimeoutsApplied:
                RequireActor(action, null);
                instance.ApplyTimeouts(at, Read<TimeoutsPayload>(action.Payload).ExpectedVersion);
                break;

            default:
                throw new InvalidDataException("Unknown or misplaced action kind.");
        }
    }

    /// <summary>
    /// A SHA-256 of one stored action row, keyed by its instance and sequence (DL-075a). The audit record carries this
    /// instead of the inputs: the append-only log keeps the evidence, the audit proves which row it was.
    /// </summary>
    public static string RowHash(Guid instanceId, int sequence, LoggedAction action, long versionAfter)
    {
        ArgumentNullException.ThrowIfNull(action);
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            "iam.workflow.action.v1",
            instanceId.ToString("D"),
            sequence,
            action.Kind.ToString(),
            action.AtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            action.Actor,
            versionAfter,
            action.Payload,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The stored actor column must name the person inside the inputs, and nobody for a system action (DL-075c).</summary>
    private static void RequireActor(LoggedAction action, PersonId? actor)
    {
        var expected = actor is { IsValid: true } person ? person.Value : null;
        if (!string.Equals(action.Actor, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An action's stored actor does not match its inputs.");
        }
    }

    private static LoggedAction Log<T>(ApprovalActionKind kind, T payload, DateTimeOffset at, PersonId? actor) =>
        new(kind, JsonSerializer.Serialize(payload, Json), at, actor is { IsValid: true } person ? person.Value : null);

    private static T Read<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("An action's stored inputs are empty.");

    private sealed record BoundPayload(ApprovalBinding Binding, IReadOnlyList<StageDefinition> Stages);

    private sealed record SuppliedPayload(Guid StageId, ApproverResolution Resolution);

    private sealed record ReturnedPayload(Guid StageId, PersonId Actor, PersonId? OnBehalfOf, string? Reason, long ExpectedVersion);

    private sealed record ClosedPayload(PersonId Actor, string? Reason, long ExpectedVersion);

    private sealed record EscalationPayload(Guid StageId, IReadOnlyList<PersonId> Candidates, PersonId Actor, string? Reason, long ExpectedVersion);

    private sealed record TimeoutsPayload(long ExpectedVersion);

    /// <summary>A person as their sign-in id. The default value, which names nobody, is null, so it replays as it was given.</summary>
    private sealed class PersonIdJsonConverter : JsonConverter<PersonId>
    {
        public override bool HandleNull => true;

        public override PersonId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return default;
            }

            return PersonId.TryCreate(reader.GetString(), out var person) ? person : throw new JsonException("Not a valid person id.");
        }

        public override void Write(Utf8JsonWriter writer, PersonId value, JsonSerializerOptions options)
        {
            if (value.IsValid)
            {
                writer.WriteStringValue(value.Value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
