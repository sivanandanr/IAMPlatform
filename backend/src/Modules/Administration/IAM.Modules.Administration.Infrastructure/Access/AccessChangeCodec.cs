using System.Text.Json;
using System.Text.Json.Serialization;
using IAM.Modules.Administration.Domain.Access;

namespace IAM.Modules.Administration.Infrastructure.Access;

/// <summary>
/// Stores an access change as a kind code plus a small JSON payload. Reading is strict: unknown fields, missing fields,
/// repeated fields, deep nesting and oversized input are refused, so a damaged or tampered row can't become a different change.
/// </summary>
public static class AccessChangeCodec
{
    public const string KindAssignRole = "AssignRole";
    public const string KindRemoveAssignment = "RemoveAssignment";
    public const string KindDefineRole = "DefineRole";
    public const string KindAddIncompatiblePair = "AddIncompatiblePair";
    public const string KindRemoveIncompatiblePair = "RemoveIncompatiblePair";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4,
        AllowDuplicateProperties = false,
    };

    public static (string Kind, string Payload) Encode(AccessChange change) => change switch
    {
        AssignRole c => (KindAssignRole, JsonSerializer.Serialize(new AssignmentPayload(c.Subject, c.RoleCode), Options)),
        RemoveAssignment c => (KindRemoveAssignment, JsonSerializer.Serialize(new AssignmentPayload(c.Subject, c.RoleCode), Options)),
        DefineRole c => (KindDefineRole, JsonSerializer.Serialize(new RolePayload(c.Code, c.Name, [.. c.Actions]), Options)),
        AddIncompatiblePair c => (KindAddIncompatiblePair, JsonSerializer.Serialize(new PairPayload(c.First, c.Second), Options)),
        RemoveIncompatiblePair c => (KindRemoveIncompatiblePair, JsonSerializer.Serialize(new PairPayload(c.First, c.Second), Options)),
        _ => throw new AccessRefusedException(AccessRefusal.InvalidChange),
    };

    public static AccessChange Decode(string kind, string payload)
    {
        if (payload is null || payload.Length > MaxPayloadLength)
        {
            throw new AccessRefusedException(AccessRefusal.InvalidChange);
        }

        try
        {
            switch (kind)
            {
                case KindAssignRole:
                    var assign = Read<AssignmentPayload>(payload);
                    return new AssignRole(assign.Subject, assign.RoleCode);
                case KindRemoveAssignment:
                    var remove = Read<AssignmentPayload>(payload);
                    return new RemoveAssignment(remove.Subject, remove.RoleCode);
                case KindDefineRole:
                    var role = Read<RolePayload>(payload);
                    return role.Actions.Any(a => a is null)
                        ? throw new AccessRefusedException(AccessRefusal.InvalidChange)
                        : new DefineRole(role.Code, role.Name, Array.AsReadOnly(role.Actions));
                case KindAddIncompatiblePair:
                    var add = Read<PairPayload>(payload);
                    return new AddIncompatiblePair(add.First, add.Second);
                case KindRemoveIncompatiblePair:
                    var drop = Read<PairPayload>(payload);
                    return new RemoveIncompatiblePair(drop.First, drop.Second);
                default:
                    throw new AccessRefusedException(AccessRefusal.InvalidChange);
            }
        }
        catch (JsonException ex)
        {
            throw new AccessRefusedException(AccessRefusal.InvalidChange, ex);
        }
    }

    /// <summary>Same bound as the stored column.</summary>
    public const int MaxPayloadLength = 4_000;

    private static T Read<T>(string payload)
        where T : class =>
        JsonSerializer.Deserialize<T>(payload, Options) ?? throw new AccessRefusedException(AccessRefusal.InvalidChange);

    private sealed record AssignmentPayload(string Subject, string RoleCode);

    private sealed record RolePayload(string Code, string Name, string[] Actions);

    private sealed record PairPayload(string First, string Second);
}
