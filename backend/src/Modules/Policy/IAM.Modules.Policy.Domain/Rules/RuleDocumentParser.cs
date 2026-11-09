using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IAM.Modules.Policy.Domain.Rules;

/// <summary>A node of an unvalidated rule tree: shape only, nothing here is known to be a real attribute or a valid literal.</summary>
internal abstract record RuleNode(string Path);

internal sealed record RuleGroupNode(string Path, RuleGroupOperator Operator, IReadOnlyList<RuleNode> Children) : RuleNode(Path);

internal sealed record RuleConditionNode(string Path, RuleSubject Subject, Guid AttributeId, RuleOperator Operator, IReadOnlyList<string> Values) : RuleNode(Path);

/// <summary>Explicit "any value" (spec K.4). True only when a fresh value exists, so missing data can never reach a broad rule (IAM-WF-018).</summary>
internal sealed record RuleWildcardNode(string Path, RuleSubject Subject, Guid AttributeId) : RuleNode(Path);

/// <summary>
/// Strict reader of a rule's JSON document (ADR-0004, BACKEND_INPUT_VALIDATION). Size is checked before parsing. Refused:
/// comments, trailing commas, duplicate or unknown properties (so a document cannot carry status, priority or similar),
/// literals that are not JSON strings, and over-deep nesting. Nothing in the document is ever executed.
/// </summary>
internal static class RuleDocumentParser
{
    /// <summary>Enough for the deepest allowed rule (object, array) per group plus the outer object and a values array.</summary>
    private const int MaxJsonDepth = (2 * RuleLimits.MaxGroupDepth) + 5;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxJsonDepth,
    };

    public static RuleNode Parse(string? json)
    {
        // Bound the work before encoding: UTF-8 is never fewer bytes than UTF-16 code units for this check to matter.
        if (json is null || json.Length > RuleLimits.MaxRuleBytes)
        {
            throw new RuleRejectedException(RuleRejection.TooLarge, "document");
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException)
        {
            throw new RuleRejectedException(RuleRejection.InvalidJson, "document");
        }

        if (bytes.Length > RuleLimits.MaxRuleBytes)
        {
            throw new RuleRejectedException(RuleRejection.TooLarge, "document");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, Options);
            return ParseDocument(document.RootElement);
        }
        catch (JsonException)
        {
            // Not kept as an inner exception: its message can quote the input, and exceptions end up in logs.
            throw new RuleRejectedException(RuleRejection.InvalidJson, "document");
        }
        catch (InvalidOperationException)
        {
            // An escaped lone surrogate is valid JSON syntax but has no string value; reading it throws this.
            throw new RuleRejectedException(RuleRejection.InvalidJson, "document");
        }
    }

    private static RuleNode ParseDocument(JsonElement root)
    {
        var properties = Properties(root, "document", "schemaVersion", "root");
        var version = Required(properties, "schemaVersion", "document");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
        {
            throw new RuleRejectedException(RuleRejection.InvalidPropertyType, "document.schemaVersion");
        }

        if (number != RuleLimits.SchemaVersion)
        {
            throw new RuleRejectedException(RuleRejection.UnsupportedSchemaVersion, "document.schemaVersion");
        }

        var count = 0;
        return ParseNode(Required(properties, "root", "document"), "root", 0, ref count);
    }

    private static RuleNode ParseNode(JsonElement element, string path, int groupDepth, ref int conditions)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new RuleRejectedException(RuleRejection.InvalidPropertyType, path);
        }

        // The node type decides which properties are allowed, so read it before checking the rest.
        var type = TypeOf(element, path);
        return type switch
        {
            "group" => ParseGroup(element, path, groupDepth, ref conditions),
            "condition" => ParseCondition(element, path, ref conditions),
            "wildcard" => ParseWildcard(element, path, ref conditions),
            _ => throw new RuleRejectedException(RuleRejection.UnknownNodeType, path),
        };
    }

    private static string? TypeOf(JsonElement element, string path)
    {
        string? type = null;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "type", StringComparison.Ordinal))
            {
                if (found)
                {
                    throw new RuleRejectedException(RuleRejection.DuplicateProperty, path);
                }

                found = true;
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new RuleRejectedException(RuleRejection.InvalidPropertyType, $"{path}.type");
                }

                type = property.Value.GetString();
            }
        }

        return found ? type : throw new RuleRejectedException(RuleRejection.MissingProperty, $"{path}.type");
    }

    private static RuleGroupNode ParseGroup(JsonElement element, string path, int groupDepth, ref int conditions)
    {
        var depth = groupDepth + 1;
        if (depth > RuleLimits.MaxGroupDepth)
        {
            throw new RuleRejectedException(RuleRejection.TooDeep, path);
        }

        var properties = Properties(element, path, "type", "operator", "children");
        var op = Text(properties, "operator", path) switch
        {
            "And" => RuleGroupOperator.And,
            "Or" => RuleGroupOperator.Or,
            _ => throw new RuleRejectedException(RuleRejection.UnknownGroupOperator, $"{path}.operator"),
        };

        var children = Required(properties, "children", path);
        if (children.ValueKind != JsonValueKind.Array)
        {
            throw new RuleRejectedException(RuleRejection.InvalidPropertyType, $"{path}.children");
        }

        if (children.GetArrayLength() == 0)
        {
            throw new RuleRejectedException(RuleRejection.EmptyGroup, path);
        }

        var nodes = new List<RuleNode>(children.GetArrayLength());
        foreach (var child in children.EnumerateArray())
        {
            nodes.Add(ParseNode(child, $"{path}.children[{nodes.Count.ToString(CultureInfo.InvariantCulture)}]", depth, ref conditions));
        }

        return new RuleGroupNode(path, op, nodes);
    }

    private static RuleConditionNode ParseCondition(JsonElement element, string path, ref int conditions)
    {
        CountCondition(path, ref conditions);
        var properties = Properties(element, path, "type", "subject", "attributeId", "operator", "values");
        var subject = Subject(properties, path);
        var attributeId = AttributeId(properties, path);
        if (!RuleVocabulary.TryOperator(Text(properties, "operator", path), out var op))
        {
            throw new RuleRejectedException(RuleRejection.UnknownOperator, $"{path}.operator");
        }

        return new RuleConditionNode(path, subject, attributeId, op, Values(properties, path));
    }

    private static RuleWildcardNode ParseWildcard(JsonElement element, string path, ref int conditions)
    {
        CountCondition(path, ref conditions);
        var properties = Properties(element, path, "type", "subject", "attributeId");
        return new RuleWildcardNode(path, Subject(properties, path), AttributeId(properties, path));
    }

    private static void CountCondition(string path, ref int conditions)
    {
        conditions++;
        if (conditions > RuleLimits.MaxConditionsPerRule)
        {
            throw new RuleRejectedException(RuleRejection.TooManyConditions, path);
        }
    }

    private static List<string> Values(Dictionary<string, JsonElement> properties, string path)
    {
        if (!properties.TryGetValue("values", out var values))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new RuleRejectedException(RuleRejection.InvalidPropertyType, $"{path}.values");
        }

        if (values.GetArrayLength() > RuleLimits.MaxListValues)
        {
            throw new RuleRejectedException(RuleRejection.TooManyValues, $"{path}.values");
        }

        var list = new List<string>(values.GetArrayLength());
        foreach (var value in values.EnumerateArray())
        {
            // Literals are JSON strings parsed against the attribute's type: no number, boolean or null shortcuts (ADR-0004).
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new RuleRejectedException(RuleRejection.InvalidPropertyType, $"{path}.values");
            }

            var text = value.GetString() ?? throw new RuleRejectedException(RuleRejection.InvalidPropertyType, $"{path}.values");
            if (text.Length > RuleLimits.MaxLiteralLength)
            {
                throw new RuleRejectedException(RuleRejection.LiteralTooLong, $"{path}.values");
            }

            list.Add(text);
        }

        return list;
    }

    private static RuleSubject Subject(Dictionary<string, JsonElement> properties, string path) =>
        RuleVocabulary.TrySubject(Text(properties, "subject", path), out var subject)
            ? subject
            : throw new RuleRejectedException(RuleRejection.UnknownSubject, $"{path}.subject");

    private static Guid AttributeId(Dictionary<string, JsonElement> properties, string path) =>
        Guid.TryParseExact(Text(properties, "attributeId", path), "D", out var id) && id != Guid.Empty
            ? id
            : throw new RuleRejectedException(RuleRejection.InvalidAttributeId, $"{path}.attributeId");

    private static string Text(Dictionary<string, JsonElement> properties, string name, string path)
    {
        var value = Required(properties, name, path);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new RuleRejectedException(RuleRejection.InvalidPropertyType, $"{path}.{name}")
            : throw new RuleRejectedException(RuleRejection.InvalidPropertyType, $"{path}.{name}");
    }

    private static JsonElement Required(Dictionary<string, JsonElement> properties, string name, string path) =>
        properties.TryGetValue(name, out var value)
            ? value
            : throw new RuleRejectedException(RuleRejection.MissingProperty, $"{path}.{name}");

    /// <summary>The object's properties, refusing duplicates (after JSON unescaping) and anything not in the allowlist.</summary>
    private static Dictionary<string, JsonElement> Properties(JsonElement element, string path, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new RuleRejectedException(RuleRejection.InvalidPropertyType, path);
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new RuleRejectedException(RuleRejection.UnknownProperty, path);
            }

            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw new RuleRejectedException(RuleRejection.DuplicateProperty, path);
            }
        }

        return properties;
    }
}
