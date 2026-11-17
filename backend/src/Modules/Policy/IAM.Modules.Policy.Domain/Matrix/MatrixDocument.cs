using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using IAM.Modules.Policy.Domain.Rules;

namespace IAM.Modules.Policy.Domain.Matrix;

/// <summary>What a stage asks of its people (spec 7.3): a decision, or a task such as giving an account name.</summary>
public enum MatrixStageKind
{
    Decision = 1,
    Action = 2,
}

/// <summary>Where a stage's people come from (IAM-WF-003). Resolved when the stage opens, never stored as names.</summary>
public enum MatrixApproverSource
{
    EntitlementOwner = 1,
    ApplicationOwner = 2,
    ApplicationRole = 3,
    Workgroup = 4,
}

/// <summary>How many approvals close a stage (IAM-WF-004); the same names as Workflow's.</summary>
public enum MatrixQuorum
{
    All = 1,
    AnyOne = 2,
    MinimumN = 3,
}

/// <summary>What a stage does at its deadline (DL-013). There is deliberately no approve.</summary>
public enum MatrixTimeout
{
    Escalate = 1,
    Reject = 2,
    Cancel = 3,
}

/// <summary>The only fields an Action stage may fill in (CLAUDE.md "Action-only fields"): names, never access, people or dates.</summary>
public enum MatrixActionField
{
    AccountName = 1,
}

/// <summary>One stage of a rule's route.</summary>
public sealed record MatrixStage(
    int Sequence,
    MatrixStageKind Kind,
    MatrixApproverSource Source,
    string? RoleTypeCode,
    string? WorkgroupKey,
    MatrixQuorum Quorum,
    int? MinimumApprovals,
    bool Mandatory,
    bool Independent,
    int DeadlineHours,
    MatrixTimeout OnTimeout,
    IReadOnlyList<MatrixActionField> Fields);

/// <summary>One rule: when it applies (a typed condition, untrusted until compiled) and the route it selects.</summary>
public sealed record MatrixRule(
    Guid RuleId,
    int Priority,
    DateTimeOffset? EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    bool AllowsWildcard,
    string ConditionJson,
    IReadOnlyList<MatrixStage> Route)
{
    /// <summary>The condition's literals may be sensitive; a logged record must not show them.</summary>
    public override string ToString() => $"Rule {RuleId:D} priority {Priority}";
}

/// <summary>A matrix version's content: its rules, each with a route (DL-090c).</summary>
public sealed record MatrixDocument(IReadOnlyList<MatrixRule> Rules);

/// <summary>A matrix input was refused. The message names where and which rule, never a value.</summary>
public sealed class MatrixRejectedException : Exception
{
    public MatrixRejectedException()
    {
    }

    public MatrixRejectedException(string message)
        : base(message)
    {
    }

    public MatrixRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MatrixRejectedException(string field, string rule, Exception? innerException = null)
        : base($"Approval matrix rejected: {field} {rule}.", innerException)
    {
        Field = field;
    }

    public string? Field { get; }
}

/// <summary>A compiled matrix: the rules engine's compiled policy plus each rule's route.</summary>
public sealed class CompiledMatrix
{
    internal CompiledMatrix(CompiledPolicy policy, IReadOnlyDictionary<Guid, IReadOnlyList<MatrixStage>> routes, RuleAnalysis analysis)
    {
        Policy = policy;
        Routes = routes;
        Analysis = analysis;
    }

    public CompiledPolicy Policy { get; }

    public IReadOnlyDictionary<Guid, IReadOnlyList<MatrixStage>> Routes { get; }

    /// <summary>The pre-publication check (DL-011): possible same-priority overlaps block publication (DL-090e).</summary>
    public RuleAnalysis Analysis { get; }

    public bool Publishable => Analysis.Overlaps.Count == 0 && !Analysis.Truncated;
}

/// <summary>Parsing, validation, the canonical form, the hash and compilation of matrix documents.</summary>
public static partial class MatrixDocuments
{
    public const int MaxDocumentBytes = 4 * 1024 * 1024;
    public const int MaxRules = 200;
    public const int MaxStages = 10;
    public const int MaxSequence = 20;
    public const int MaxMinimumApprovals = 32;
    public const int MaxDeadlineHours = 2160;

    /// <summary>Part of the hashed text, so the layout can change later without ambiguity.</summary>
    public const string ContentHashLayout = "iam.policy.approval-matrix.v1";

    private static readonly JsonDocumentOptions Strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 };
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] DocumentProperties = ["rules"];
    private static readonly string[] RuleProperties = ["ruleId", "priority", "effectiveFromUtc", "effectiveToUtc", "allowsWildcard", "condition", "route"];
    private static readonly string[] StageProperties = ["sequence", "kind", "source", "roleTypeCode", "workgroupKey", "quorum", "minimumApprovals", "mandatory", "independent", "deadlineHours", "onTimeout", "fields"];

    /// <summary>Reads a matrix document strictly: unknown or duplicate properties, wrong types and oversize input are refused.</summary>
    public static MatrixDocument Parse(string? json)
    {
        if (json is null || json.Length > MaxDocumentBytes)
        {
            throw new MatrixRejectedException("document", "is too large");
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException)
        {
            throw new MatrixRejectedException("document", "is not valid text");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, Strict);
            var root = Object(document.RootElement, DocumentProperties, "document");
            var rules = Array(root["rules"], "rules");
            if (rules.Count is 0 or > MaxRules)
            {
                throw new MatrixRejectedException("rules", $"must be 1-{MaxRules} rules");
            }

            return new MatrixDocument([.. rules.Select((r, i) => ParseRule(r, $"rules[{i}]"))]);
        }
        catch (JsonException)
        {
            // Not kept as an inner exception: its message can quote the input.
            throw new MatrixRejectedException("document", "is not valid JSON");
        }
    }

    /// <summary>Checks everything about routes that doesn't need the catalog. Conditions are checked by <see cref="Compile"/>.</summary>
    public static void Validate(MatrixDocument? document)
    {
        if (document?.Rules is null || document.Rules.Count is 0 or > MaxRules)
        {
            throw new MatrixRejectedException("rules", $"must be 1-{MaxRules} rules");
        }

        var ids = new HashSet<Guid>();
        for (var i = 0; i < document.Rules.Count; i++)
        {
            var rule = document.Rules[i];
            var where = $"rules[{i}]";
            if (rule is null || rule.RuleId == Guid.Empty || !ids.Add(rule.RuleId))
            {
                throw new MatrixRejectedException(where, "must have a unique rule id");
            }

            if (rule.Priority is < 0 or > RuleLimits.MaxPriority)
            {
                throw new MatrixRejectedException(where, $"priority must be 0-{RuleLimits.MaxPriority}");
            }

            if (rule.EffectiveFromUtc is { } from && rule.EffectiveToUtc is { } to && to <= from)
            {
                throw new MatrixRejectedException(where, "must end after it starts");
            }

            ValidateRoute(rule.Route, where);
        }
    }

    /// <summary>
    /// The canonical stored form: compact JSON with a fixed property order, the condition as given (compacted). Reading it
    /// back and writing it again gives the same text, so the hash covers exactly what is stored.
    /// </summary>
    public static string ToStored(MatrixDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("rules");
            foreach (var rule in document.Rules)
            {
                writer.WriteStartObject();
                writer.WriteString("ruleId", rule.RuleId.ToString("D"));
                writer.WriteNumber("priority", rule.Priority);
                WriteTime(writer, "effectiveFromUtc", rule.EffectiveFromUtc);
                WriteTime(writer, "effectiveToUtc", rule.EffectiveToUtc);
                writer.WriteBoolean("allowsWildcard", rule.AllowsWildcard);
                writer.WritePropertyName("condition");
                using (var condition = JsonDocument.Parse(rule.ConditionJson, Strict))
                {
                    condition.RootElement.WriteTo(writer);
                }

                writer.WriteStartArray("route");
                foreach (var stage in rule.Route)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("sequence", stage.Sequence);
                    writer.WriteString("kind", stage.Kind.ToString());
                    writer.WriteString("source", stage.Source.ToString());
                    WriteText(writer, "roleTypeCode", stage.RoleTypeCode);
                    WriteText(writer, "workgroupKey", stage.WorkgroupKey);
                    writer.WriteString("quorum", stage.Quorum.ToString());
                    if (stage.MinimumApprovals is { } n)
                    {
                        writer.WriteNumber("minimumApprovals", n);
                    }
                    else
                    {
                        writer.WriteNull("minimumApprovals");
                    }

                    writer.WriteBoolean("mandatory", stage.Mandatory);
                    writer.WriteBoolean("independent", stage.Independent);
                    writer.WriteNumber("deadlineHours", stage.DeadlineHours);
                    writer.WriteString("onTimeout", stage.OnTimeout.ToString());
                    writer.WriteStartArray("fields");
                    foreach (var field in stage.Fields)
                    {
                        writer.WriteStringValue(field.ToString());
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Lower-case hex SHA-256 over the UTF-8 compact JSON array <c>[layout, versionNumber, storedDocument]</c>.</summary>
    public static string ComputeContentHash(int versionNumber, string storedDocument)
    {
        ArgumentNullException.ThrowIfNull(storedDocument);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(ContentHashLayout);
            writer.WriteNumberValue(versionNumber);
            writer.WriteStringValue(storedDocument);
            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>
    /// Compiles every condition against the catalog (published attributes plus the built-in facts) with the rules engine,
    /// and runs the pre-publication overlap check. Any refused condition refuses the whole document.
    /// </summary>
    public static CompiledMatrix Compile(Guid versionId, MatrixDocument document, IReadOnlyDictionary<Guid, AttributeSpec> catalog)
    {
        Validate(document);
        CompiledPolicy policy;
        try
        {
            policy = PolicyCompiler.Compile(
                new PolicyVersionDefinition(versionId, [.. document.Rules.Select(r => new RuleDefinition(r.RuleId, r.Priority, r.EffectiveFromUtc, r.EffectiveToUtc, r.AllowsWildcard, r.ConditionJson))]),
                catalog);
        }
        catch (RuleRejectedException ex)
        {
            throw new MatrixRejectedException("rules", $"has a condition the rules engine refuses ({ex.Rejection})", ex);
        }

        return new CompiledMatrix(policy, document.Rules.ToDictionary(r => r.RuleId, r => r.Route), RuleOverlapAnalyzer.Analyze(policy));
    }

    /// <summary>The attribute ids a document's conditions name, read from the documents without trusting them.</summary>
    public static IReadOnlySet<Guid> AttributeIds(MatrixDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var ids = new HashSet<Guid>();
        foreach (var rule in document.Rules)
        {
            using var condition = JsonDocument.Parse(rule.ConditionJson, Strict);
            Collect(condition.RootElement, ids);
        }

        return ids;
    }

    private static void Collect(JsonElement element, HashSet<Guid> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("attributeId") && property.Value.ValueKind == JsonValueKind.String && Guid.TryParseExact(property.Value.GetString(), "D", out var id))
                    {
                        ids.Add(id);
                    }
                    else
                    {
                        Collect(property.Value, ids);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, ids);
                }

                break;
        }
    }

    private static void ValidateRoute(IReadOnlyList<MatrixStage>? route, string where)
    {
        if (route is null || route.Count is 0 or > MaxStages)
        {
            throw new MatrixRejectedException(where, $"route must have 1-{MaxStages} stages");
        }

        // Discretionary access always needs an approval (CLAUDE.md rule 1): a route of tasks alone grants nothing.
        if (!route.Any(s => s?.Kind == MatrixStageKind.Decision))
        {
            throw new MatrixRejectedException(where, "route must have at least one Decision stage");
        }

        foreach (var stage in route)
        {
            if (stage is null
                || stage.Sequence is < 1 or > MaxSequence
                || !Enum.IsDefined(stage.Kind) || !Enum.IsDefined(stage.Source) || !Enum.IsDefined(stage.Quorum) || !Enum.IsDefined(stage.OnTimeout)
                || stage.DeadlineHours is < 1 or > MaxDeadlineHours)
            {
                throw new MatrixRejectedException(where, "has a stage with an invalid sequence, kind, source, quorum, deadline or timeout");
            }

            var sourceOk = stage.Source switch
            {
                MatrixApproverSource.ApplicationRole => KeyPattern().IsMatch(stage.RoleTypeCode ?? string.Empty) && stage.WorkgroupKey is null,
                MatrixApproverSource.Workgroup => KeyPattern().IsMatch(stage.WorkgroupKey ?? string.Empty) && stage.RoleTypeCode is null,
                _ => stage.RoleTypeCode is null && stage.WorkgroupKey is null,
            };
            if (!sourceOk)
            {
                throw new MatrixRejectedException(where, "has a stage whose source needs exactly its own role type code or workgroup key");
            }

            if (stage.Quorum == MatrixQuorum.MinimumN ? stage.MinimumApprovals is not (>= 1 and <= MaxMinimumApprovals) : stage.MinimumApprovals is not null)
            {
                throw new MatrixRejectedException(where, $"has a stage whose minimum approvals must be 1-{MaxMinimumApprovals} for MinimumN and absent otherwise");
            }

            if (stage.Fields is null || stage.Fields.Any(f => !Enum.IsDefined(f)) || stage.Fields.Distinct().Count() != stage.Fields.Count)
            {
                throw new MatrixRejectedException(where, "has a stage with unknown or repeated fields");
            }

            // An Action stage is a task: one person does it, and it can't be a mandatory approval. A Decision stage fills nothing in.
            if (stage.Kind == MatrixStageKind.Action
                ? stage.Quorum != MatrixQuorum.AnyOne || stage.Mandatory || stage.Independent || stage.Fields.Count == 0 || stage.OnTimeout == MatrixTimeout.Reject
                : stage.Fields.Count > 0)
            {
                throw new MatrixRejectedException(where, "has an Action stage that isn't a single-person task listing its fields, or a Decision stage with fields");
            }
        }
    }

    private static MatrixRule ParseRule(JsonElement element, string where)
    {
        var p = Object(element, RuleProperties, where);
        var condition = p["condition"];
        if (condition.ValueKind != JsonValueKind.Object)
        {
            throw new MatrixRejectedException(where, "condition must be an object");
        }

        var route = Array(p["route"], where + ".route");
        return new MatrixRule(
            RuleId(p["ruleId"], where),
            Int(p["priority"], where),
            Time(p["effectiveFromUtc"], where),
            Time(p["effectiveToUtc"], where),
            Bool(p["allowsWildcard"], where),
            condition.GetRawText(),
            [.. route.Select((s, i) => ParseStage(s, $"{where}.route[{i}]"))]);
    }

    private static MatrixStage ParseStage(JsonElement element, string where)
    {
        var p = Object(element, StageProperties, where);
        var fields = Array(p["fields"], where);
        return new MatrixStage(
            Int(p["sequence"], where),
            Name<MatrixStageKind>(p["kind"], where),
            Name<MatrixApproverSource>(p["source"], where),
            OptionalText(p["roleTypeCode"], where),
            OptionalText(p["workgroupKey"], where),
            Name<MatrixQuorum>(p["quorum"], where),
            p["minimumApprovals"].ValueKind == JsonValueKind.Null ? null : Int(p["minimumApprovals"], where),
            Bool(p["mandatory"], where),
            Bool(p["independent"], where),
            Int(p["deadlineHours"], where),
            Name<MatrixTimeout>(p["onTimeout"], where),
            [.. fields.Select(f => Name<MatrixActionField>(f, where))]);
    }

    /// <summary>All allowed properties must be present exactly once; nothing else may be.</summary>
    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] allowed, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new MatrixRejectedException(where, "must be an object");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !properties.TryAdd(property.Name, property.Value))
            {
                throw new MatrixRejectedException(where, "has an unknown or repeated property");
            }
        }

        if (properties.Count != allowed.Length)
        {
            throw new MatrixRejectedException(where, "is missing a property");
        }

        return properties;
    }

    private static List<JsonElement> Array(JsonElement element, string where) =>
        element.ValueKind == JsonValueKind.Array ? [.. element.EnumerateArray()] : throw new MatrixRejectedException(where, "must be an array");

    private static Guid RuleId(JsonElement element, string where) =>
        element.ValueKind == JsonValueKind.String && System.Guid.TryParseExact(element.GetString(), "D", out var id) ? id : throw new MatrixRejectedException(where, "ruleId must be a GUID");

    private static int Int(JsonElement element, string where) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : throw new MatrixRejectedException(where, "has a number that isn't a whole number");

    private static bool Bool(JsonElement element, string where) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new MatrixRejectedException(where, "has a flag that isn't true or false"),
    };

    private static DateTimeOffset? Time(JsonElement element, string where)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParseExact(element.GetString(), "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : throw new MatrixRejectedException(where, "has a time that isn't yyyy-MM-ddTHH:mm:ssZ");
    }

    private static string? OptionalText(JsonElement element, string where) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        _ => throw new MatrixRejectedException(where, "has a code that isn't text"),
    };

    private static TEnum Name<TEnum>(JsonElement element, string where)
        where TEnum : struct, Enum =>
        element.ValueKind == JsonValueKind.String && Enum.GetNames<TEnum>().Contains(element.GetString(), StringComparer.Ordinal)
            ? Enum.Parse<TEnum>(element.GetString()!)
            : throw new MatrixRejectedException(where, $"must be one of {string.Join(", ", Enum.GetNames<TEnum>())}");

    private static void WriteTime(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } time)
        {
            writer.WriteString(name, time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyPattern();
}

/// <summary>
/// The built-in fact attributes (DL-090f): facts about a request, its entitlement and its application that the Request
/// module fills from the catalog and the item. Defined in code with stable ids, like the built-in role types, because the
/// attribute catalog publishes only through a second person. Their definitions are part of every compiled matrix's hash.
/// </summary>
public static class BuiltInFacts
{
    public static readonly AttributeSpec RequestAction = Code("0193c000-0000-7000-8000-000000000001", RuleSubject.Request, 32);
    public static readonly AttributeSpec RequestAccountType = Code("0193c000-0000-7000-8000-000000000002", RuleSubject.Request, 16);
    public static readonly AttributeSpec RequestNewAccount = Flag("0193c000-0000-7000-8000-000000000003", RuleSubject.Request);
    public static readonly AttributeSpec EntitlementKey = Code("0193c000-0000-7000-8000-000000000011", RuleSubject.Entitlement, 64);
    public static readonly AttributeSpec EntitlementPrivileged = Flag("0193c000-0000-7000-8000-000000000012", RuleSubject.Entitlement);
    public static readonly AttributeSpec EntitlementRisk = Code("0193c000-0000-7000-8000-000000000013", RuleSubject.Entitlement, 16);
    public static readonly AttributeSpec EntitlementType = Code("0193c000-0000-7000-8000-000000000014", RuleSubject.Entitlement, 32);
    public static readonly AttributeSpec ApplicationKey = Code("0193c000-0000-7000-8000-000000000021", RuleSubject.Application, 64);

    /// <summary>Key, display name and spec of each built-in fact, in a fixed order.</summary>
    public static IReadOnlyList<(string Key, string DisplayName, AttributeSpec Spec)> All { get; } =
    [
        ("request_action", "Request action", RequestAction),
        ("request_account_type", "Target account type", RequestAccountType),
        ("request_new_account", "Needs a new account", RequestNewAccount),
        ("entitlement_key", "Entitlement", EntitlementKey),
        ("entitlement_privileged", "Privileged entitlement", EntitlementPrivileged),
        ("entitlement_risk", "Entitlement risk", EntitlementRisk),
        ("entitlement_type", "Entitlement type", EntitlementType),
        ("application_key", "Application", ApplicationKey),
    ];

    private static AttributeSpec Code(string id, RuleSubject scope, int maxLength) => Spec(id, scope, RuleDataType.Code, maxLength,
        [RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.In, RuleOperator.NotIn]);

    private static AttributeSpec Flag(string id, RuleSubject scope) => Spec(id, scope, RuleDataType.Boolean, null, [RuleOperator.Equals, RuleOperator.NotEquals]);

    private static AttributeSpec Spec(string id, RuleSubject scope, RuleDataType type, int? maxLength, RuleOperator[] operators)
    {
        var attributeId = System.Guid.Parse(id);

        // Version and hash are fixed by this definition: changing a built-in fact means a new id, never an edit.
        var definition = $"iam.policy.built-in-fact.v1|{attributeId:D}|{scope}|{type}|{maxLength}|{string.Join(',', operators)}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));
        return new AttributeSpec(attributeId, attributeId, hash, scope, type, RuleSensitivity.Internal, RuleNormalization.None, 5, maxLength, operators.ToHashSet());
    }
}
