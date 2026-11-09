using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace IAM.Modules.Policy.Domain.Rules;

/// <summary>One rule of a policy version as authored. <see cref="ConditionJson"/> is untrusted until compiled.</summary>
public sealed record RuleDefinition(
    Guid RuleId,
    int Priority,
    DateTimeOffset? EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    bool AllowsWildcard,
    string ConditionJson)
{
    /// <summary>The document's literals may be sensitive; a logged record must not show them.</summary>
    public override string ToString() => $"Rule {RuleId:D} priority {Priority}";
}

/// <summary>A policy version as authored, before validation.</summary>
public sealed record PolicyVersionDefinition(Guid VersionId, IReadOnlyList<RuleDefinition> Rules);

internal abstract record CompiledNode(string Path);

internal sealed record CompiledGroup(string Path, RuleGroupOperator Operator, IReadOnlyList<CompiledNode> Children) : CompiledNode(Path);

internal sealed record CompiledCondition(
    string Path,
    RuleSubject Subject,
    AttributeSpec Attribute,
    RuleOperator Operator,
    IReadOnlyList<TypedValue> Literals,
    IReadOnlySet<string> LiteralSet) : CompiledNode(Path);

internal sealed record CompiledWildcard(string Path, RuleSubject Subject, AttributeSpec Attribute) : CompiledNode(Path);

/// <summary>A validated rule. Effective period: from is inclusive, to is exclusive. Only <see cref="PolicyCompiler"/> creates one.</summary>
public sealed class CompiledRule
{
    internal CompiledRule(Guid ruleId, int priority, DateTimeOffset? effectiveFromUtc, DateTimeOffset? effectiveToUtc, CompiledNode root)
    {
        RuleId = ruleId;
        Priority = priority;
        EffectiveFromUtc = effectiveFromUtc;
        EffectiveToUtc = effectiveToUtc;
        Root = root;
    }

    public Guid RuleId { get; }

    public int Priority { get; }

    public DateTimeOffset? EffectiveFromUtc { get; }

    public DateTimeOffset? EffectiveToUtc { get; }

    internal CompiledNode Root { get; }
}

/// <summary>
/// A validated policy version: rules are ordered by priority (highest first), then id. Only <see cref="PolicyCompiler"/>
/// creates one and nothing can change it afterwards, so the hash always describes the rules and their order is safe to
/// rely on. A class rather than a record: <c>with</c> could otherwise keep the hash while replacing the rules.
/// </summary>
public sealed class CompiledPolicy
{
    internal CompiledPolicy(Guid versionId, string contentHash, IReadOnlyList<CompiledRule> rules, IReadOnlyList<AttributeSpec> attributes)
    {
        VersionId = versionId;
        ContentHash = contentHash;
        Rules = Array.AsReadOnly([.. rules]);
        Attributes = Array.AsReadOnly([.. attributes]);
    }

    public Guid VersionId { get; }

    public string ContentHash { get; }

    public IReadOnlyList<CompiledRule> Rules { get; }

    public IReadOnlyList<AttributeSpec> Attributes { get; }
}

/// <summary>
/// Validates a policy version against the catalog and produces its compiled form and content hash (ADR-0004, IAM-WF-015).
/// Everything is checked before any rule can run: known attributes, operators allowed for both the type and the catalog
/// entry, literals of the right type. Text attributes are refused in rules: comparing free text cannot tell a different
/// value from the same value written differently (DL-040). The hash is over this code's own canonical bytes (ADR-0017),
/// and covers the attribute versions and everything about them that affects a decision.
/// </summary>
public static class PolicyCompiler
{
    public static CompiledPolicy Compile(PolicyVersionDefinition definition, IReadOnlyDictionary<Guid, AttributeSpec> catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalog);
        if (definition.Rules is null || definition.Rules.Count == 0)
        {
            throw new RuleRejectedException(RuleRejection.EmptyPolicy, "version");
        }

        if (definition.Rules.Count > RuleLimits.MaxRulesPerVersion)
        {
            throw new RuleRejectedException(RuleRejection.TooManyRules, "version");
        }

        var ids = new HashSet<Guid>();
        var rules = new List<CompiledRule>(definition.Rules.Count);
        foreach (var rule in definition.Rules)
        {
            if (rule is null)
            {
                throw new RuleRejectedException(RuleRejection.EmptyPolicy, "version");
            }

            if (rule.RuleId == Guid.Empty || !ids.Add(rule.RuleId))
            {
                throw new RuleRejectedException(RuleRejection.DuplicateRuleId, "version");
            }

            rules.Add(CompileRule(rule, catalog));
        }

        var ordered = rules.OrderByDescending(r => r.Priority).ThenBy(r => r.RuleId).ToList();
        var attributes = new Dictionary<Guid, AttributeSpec>();
        foreach (var rule in ordered)
        {
            CollectAttributes(rule.Root, attributes);
        }

        return new CompiledPolicy(
            definition.VersionId,
            Hash(definition.VersionId, ordered),
            ordered,
            [.. attributes.Values.OrderBy(a => a.AttributeId)]);
    }

    private static CompiledRule CompileRule(RuleDefinition rule, IReadOnlyDictionary<Guid, AttributeSpec> catalog)
    {
        if (rule.Priority is < 0 or > RuleLimits.MaxPriority)
        {
            throw new RuleRejectedException(RuleRejection.InvalidPriority, "rule");
        }

        if (rule.EffectiveFromUtc is { } from && rule.EffectiveToUtc is { } to && from >= to)
        {
            throw new RuleRejectedException(RuleRejection.InvalidEffectivePeriod, "rule");
        }

        var root = RuleDocumentParser.Parse(rule.ConditionJson);
        return new CompiledRule(rule.RuleId, rule.Priority, rule.EffectiveFromUtc, rule.EffectiveToUtc, CompileNode(root, rule.AllowsWildcard, catalog));
    }

    private static CompiledNode CompileNode(RuleNode node, bool allowsWildcard, IReadOnlyDictionary<Guid, AttributeSpec> catalog) => node switch
    {
        RuleGroupNode group => new CompiledGroup(group.Path, group.Operator, [.. group.Children.Select(c => CompileNode(c, allowsWildcard, catalog))]),
        RuleConditionNode condition => CompileCondition(condition, catalog),
        RuleWildcardNode wildcard => CompileWildcard(wildcard, allowsWildcard, catalog),
        _ => throw new RuleRejectedException(RuleRejection.UnknownNodeType, node.Path),
    };

    private static CompiledWildcard CompileWildcard(RuleWildcardNode node, bool allowsWildcard, IReadOnlyDictionary<Guid, AttributeSpec> catalog)
    {
        // Wildcards are permitted only where the rule explicitly allows them (spec K.4).
        if (!allowsWildcard)
        {
            throw new RuleRejectedException(RuleRejection.WildcardNotAllowed, node.Path);
        }

        return new CompiledWildcard(node.Path, node.Subject, Attribute(node.Path, node.Subject, node.AttributeId, catalog));
    }

    private static CompiledCondition CompileCondition(RuleConditionNode node, IReadOnlyDictionary<Guid, AttributeSpec> catalog)
    {
        var spec = Attribute(node.Path, node.Subject, node.AttributeId, catalog);
        if (!RuleVocabulary.AllowedOperators(spec.DataType).Contains(node.Operator) || !spec.Operators.Contains(node.Operator))
        {
            throw new RuleRejectedException(RuleRejection.OperatorNotAllowed, $"{node.Path}.operator");
        }

        var count = node.Values.Count;
        var valid = node.Operator switch
        {
            RuleOperator.IsEmpty or RuleOperator.IsNotEmpty => count == 0,
            RuleOperator.In or RuleOperator.NotIn => count >= 1,
            _ => count == 1,
        };
        if (!valid)
        {
            throw new RuleRejectedException(RuleRejection.WrongValueCount, $"{node.Path}.values");
        }

        var literals = new List<TypedValue>(count);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in node.Values)
        {
            if (!AttributeValueCodec.TryParse(spec, raw, out var literal))
            {
                throw new RuleRejectedException(RuleRejection.InvalidLiteral, $"{node.Path}.values");
            }

            if (!set.Add(literal.Canonical))
            {
                throw new RuleRejectedException(RuleRejection.DuplicateLiteral, $"{node.Path}.values");
            }

            literals.Add(literal);
        }

        return new CompiledCondition(node.Path, node.Subject, spec, node.Operator, literals, set);
    }

    private static AttributeSpec Attribute(string path, RuleSubject subject, Guid attributeId, IReadOnlyDictionary<Guid, AttributeSpec> catalog)
    {
        // The entry must be for this id: "attributeId is authoritative" (ADR-0004), whatever the dictionary was given.
        if (!catalog.TryGetValue(attributeId, out var spec) || spec.AttributeId != attributeId)
        {
            throw new RuleRejectedException(RuleRejection.UnknownAttribute, $"{path}.attributeId");
        }

        if (spec.DataType == RuleDataType.Text)
        {
            throw new RuleRejectedException(RuleRejection.AttributeTypeNotAllowed, $"{path}.attributeId");
        }

        // A person's attributes can be read for either person in the request (spec L.7); other scopes only for themselves.
        var allowed = subject == spec.Scope || (spec.Scope == RuleSubject.Beneficiary && subject == RuleSubject.Requester);
        return allowed ? spec : throw new RuleRejectedException(RuleRejection.SubjectNotAllowed, $"{path}.subject");
    }

    private static void CollectAttributes(CompiledNode node, Dictionary<Guid, AttributeSpec> attributes)
    {
        switch (node)
        {
            case CompiledGroup group:
                foreach (var child in group.Children)
                {
                    CollectAttributes(child, attributes);
                }

                break;
            case CompiledCondition condition:
                attributes.TryAdd(condition.Attribute.AttributeId, condition.Attribute);
                break;
            case CompiledWildcard wildcard:
                attributes.TryAdd(wildcard.Attribute.AttributeId, wildcard.Attribute);
                break;
        }
    }

    private static string Hash(Guid versionId, List<CompiledRule> rules)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", RuleLimits.SchemaVersion);
            writer.WriteString("versionId", versionId.ToString("D"));
            writer.WriteStartArray("rules");
            foreach (var rule in rules)
            {
                writer.WriteStartObject();
                writer.WriteString("ruleId", rule.RuleId.ToString("D"));
                writer.WriteNumber("priority", rule.Priority);
                writer.WriteString("from", Moment(rule.EffectiveFromUtc));
                writer.WriteString("to", Moment(rule.EffectiveToUtc));
                writer.WritePropertyName("root");
                WriteNode(writer, rule.Root);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteNode(Utf8JsonWriter writer, CompiledNode node)
    {
        writer.WriteStartObject();
        switch (node)
        {
            case CompiledGroup group:
                writer.WriteString("group", group.Operator.ToString());
                writer.WriteStartArray("children");
                foreach (var child in group.Children)
                {
                    WriteNode(writer, child);
                }

                writer.WriteEndArray();
                break;
            case CompiledCondition condition:
                WriteAttribute(writer, condition.Subject, condition.Attribute);
                writer.WriteString("operator", condition.Operator.ToString());
                writer.WriteStartArray("values");

                // List order has no meaning, so it must not change the hash.
                foreach (var value in condition.LiteralSet.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
                break;
            case CompiledWildcard wildcard:
                WriteAttribute(writer, wildcard.Subject, wildcard.Attribute);
                writer.WriteBoolean("wildcard", true);
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteAttribute(Utf8JsonWriter writer, RuleSubject subject, AttributeSpec attribute)
    {
        writer.WriteString("subject", subject.ToString());
        writer.WriteString("attributeId", attribute.AttributeId.ToString("D"));
        writer.WriteString("attributeVersionId", attribute.VersionId.ToString("D"));
        writer.WriteString("attributeHash", attribute.ContentHash);

        // Written out too, rather than trusting the catalog's hash to cover them: all of these change a decision.
        writer.WriteString("scope", attribute.Scope.ToString());
        writer.WriteString("dataType", attribute.DataType.ToString());
        writer.WriteString("sensitivity", attribute.Sensitivity.ToString());
        writer.WriteString("normalization", attribute.Normalization.ToString());
        writer.WriteNumber("freshnessMaxAgeMinutes", attribute.FreshnessMaxAgeMinutes);
        writer.WriteNumber("maxLength", attribute.MaxLength ?? 0);
        writer.WriteStartArray("operators");
        foreach (var op in attribute.Operators.Select(o => o.ToString()).Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(op);
        }

        writer.WriteEndArray();
    }

    private static string Moment(DateTimeOffset? value) =>
        value is { } moment ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) : string.Empty;
}
