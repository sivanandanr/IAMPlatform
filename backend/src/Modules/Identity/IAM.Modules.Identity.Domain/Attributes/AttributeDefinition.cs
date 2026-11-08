using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace IAM.Modules.Identity.Domain.Attributes;

/// <summary>
/// One registered attribute of the shared catalog (IAM-WF-014, spec L.4, ADR-0015). Its <see cref="Id"/> and
/// <see cref="Key"/> never change; its content lives in numbered <see cref="Versions"/>. At most one version is a
/// draft. Publishing is locked in S1-08 (DL-007).
/// </summary>
public sealed class AttributeDefinition
{
    private readonly List<AttributeDefinitionVersion> _versions = [];

    private AttributeDefinition()
    {
        Key = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Immutable key, for example <c>ee_subgroup_code</c>. Enforced in the domain and by a database trigger.</summary>
    public string Key { get; private set; }

    public IReadOnlyList<AttributeDefinitionVersion> Versions => _versions;

    /// <summary>The editable version, if any.</summary>
    public AttributeDefinitionVersion? CurrentDraft => _versions.SingleOrDefault(v => v.State == AttributeVersionState.Draft);

    /// <summary>Registers a new attribute with draft version 1.</summary>
    public static AttributeDefinition Register(string key, AttributeDraft draft)
    {
        AttributeRules.ValidateKey(key);
        AttributeRules.ValidateDraft(draft);
        var definition = new AttributeDefinition { Id = Guid.CreateVersion7(), Key = key };
        definition._versions.Add(AttributeDefinitionVersion.CreateDraft(definition, 1, draft));
        return definition;
    }

    /// <summary>Replaces the content of the current draft. Returns false if nothing changed.</summary>
    public bool UpdateDraft(AttributeDraft draft)
    {
        AttributeRules.ValidateDraft(draft);
        var current = CurrentDraft ?? throw new AttributeDefinitionRejectedException("Version", "has no draft to edit");
        return current.Apply(Key, draft);
    }

    /// <summary>Always refused in S1-08: activation needs an independent second person (DL-007, IAM-WF-019).</summary>
    public void Publish()
    {
        _ = CurrentDraft ?? throw new AttributeDefinitionRejectedException("Version", "has no draft to publish");
        throw new AttributeActivationLockedException();
    }
}

/// <summary>
/// One numbered version of an attribute definition. Drafts are editable; published and retired versions are immutable
/// (database triggers refuse to change or delete them and their operators).
/// </summary>
public sealed class AttributeDefinitionVersion
{
    /// <summary>Part of the hashed text, so the hash layout can change later without ambiguity.</summary>
    public const string ContentHashLayout = "iam.identity.attribute.v1";

    private readonly List<AttributeDefinitionVersionOperator> _operators = [];

    private AttributeDefinitionVersion()
    {
        DisplayName = string.Empty;
        SourceSystem = string.Empty;
        ContentHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid AttributeDefinitionId { get; private set; }

    public int VersionNumber { get; private set; }

    public AttributeVersionState State { get; private set; }

    public string DisplayName { get; private set; }

    public AttributeDataType DataType { get; private set; }

    public AttributeSubjectScope SubjectScope { get; private set; }

    public string SourceSystem { get; private set; }

    public AttributeSensitivity Sensitivity { get; private set; }

    public AttributeNormalization Normalization { get; private set; }

    public int FreshnessMaxAgeMinutes { get; private set; }

    public int? MaxLength { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the canonical content (see <see cref="ComputeContentHash"/>).</summary>
    public string ContentHash { get; private set; }

    public IReadOnlyList<AttributeDefinitionVersionOperator> Operators => _operators;

    /// <summary>The operators as a sorted list.</summary>
    public IReadOnlyList<AttributeOperator> OperatorList => _operators.Select(o => o.Operator).Order().ToList();

    internal static AttributeDefinitionVersion CreateDraft(AttributeDefinition definition, int versionNumber, AttributeDraft draft)
    {
        var version = new AttributeDefinitionVersion
        {
            Id = Guid.CreateVersion7(),
            AttributeDefinitionId = definition.Id,
            VersionNumber = versionNumber,
            State = AttributeVersionState.Draft,
        };
        version.Apply(definition.Key, draft);
        return version;
    }

    /// <summary>
    /// Lower-case hex SHA-256 over the UTF-8 compact JSON array
    /// <c>[layout, key, versionNumber, displayName, dataType, subjectScope, sourceSystem, sensitivity, normalization,
    /// freshnessMaxAgeMinutes, maxLength, [operators sorted by name]]</c>, enum values written by name.
    /// </summary>
    public static string ComputeContentHash(string key, int versionNumber, AttributeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(draft);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(ContentHashLayout);
            writer.WriteStringValue(key);
            writer.WriteNumberValue(versionNumber);
            writer.WriteStringValue(draft.DisplayName);
            writer.WriteStringValue(draft.DataType.ToString());
            writer.WriteStringValue(draft.SubjectScope.ToString());
            writer.WriteStringValue(draft.SourceSystem);
            writer.WriteStringValue(draft.Sensitivity.ToString());
            writer.WriteStringValue(draft.Normalization.ToString());
            writer.WriteNumberValue(draft.FreshnessMaxAgeMinutes);
            if (draft.MaxLength is { } maxLength)
            {
                writer.WriteNumberValue(maxLength);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteStartArray();
            foreach (var name in draft.Operators.Select(o => o.ToString()).Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>The current content as a draft record.</summary>
    public AttributeDraft ToDraft() => new(
        DisplayName, DataType, SubjectScope, SourceSystem, Sensitivity, Normalization, FreshnessMaxAgeMinutes, MaxLength, OperatorList);

    internal bool Apply(string key, AttributeDraft draft)
    {
        if (State != AttributeVersionState.Draft)
        {
            throw new AttributeDefinitionRejectedException("Version", "is not a draft and cannot change");
        }

        var hash = ComputeContentHash(key, VersionNumber, draft);
        if (hash == ContentHash)
        {
            return false;
        }

        DisplayName = draft.DisplayName;
        DataType = draft.DataType;
        SubjectScope = draft.SubjectScope;
        SourceSystem = draft.SourceSystem;
        Sensitivity = draft.Sensitivity;
        Normalization = draft.Normalization;
        FreshnessMaxAgeMinutes = draft.FreshnessMaxAgeMinutes;
        MaxLength = draft.MaxLength;
        ContentHash = hash;

        // Change the operator rows by difference, so unchanged rows keep their identity.
        var wanted = draft.Operators.ToHashSet();
        _operators.RemoveAll(o => !wanted.Contains(o.Operator));
        foreach (var op in wanted.Where(op => _operators.All(o => o.Operator != op)))
        {
            _operators.Add(new AttributeDefinitionVersionOperator(Id, op));
        }

        return true;
    }
}

/// <summary>One operator a version allows. Stored as a row, not JSON (ADR-0015).</summary>
public sealed class AttributeDefinitionVersionOperator
{
    internal AttributeDefinitionVersionOperator(Guid attributeDefinitionVersionId, AttributeOperator @operator)
    {
        AttributeDefinitionVersionId = attributeDefinitionVersionId;
        Operator = @operator;
    }

    private AttributeDefinitionVersionOperator()
    {
    }

    public Guid AttributeDefinitionVersionId { get; private set; }

    public AttributeOperator Operator { get; private set; }
}
