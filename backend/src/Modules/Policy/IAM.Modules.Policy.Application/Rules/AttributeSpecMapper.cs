using IAM.Modules.Identity.Contracts;
using IAM.Modules.Policy.Domain.Rules;

namespace IAM.Modules.Policy.Application.Rules;

/// <summary>
/// Maps a published catalog attribute (Identity's contract, ADR-0015) to what the Policy engine needs. Strict: an unknown
/// data type, scope, sensitivity, normalisation or operator is refused rather than guessed. Nothing can be published yet
/// (DL-007), so in this story only tests call it.
/// </summary>
public static class AttributeSpecMapper
{
    /// <summary>The catalog's own limits (Identity's AttributeRules), repeated because Application sees only its contract.</summary>
    private const int MaxFreshnessMinutes = 525_600;
    private const int MaxValueLength = 400;
    private const int HashLength = 64;

    public static AttributeSpec Map(PublishedAttributeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!RuleVocabulary.TryDataType(definition.DataType, out var dataType)
            || !RuleVocabulary.TrySubject(definition.SubjectScope, out var scope)
            || !RuleVocabulary.TrySensitivity(definition.Sensitivity, out var sensitivity)
            || !RuleVocabulary.TryNormalization(definition.Normalization, out var normalization)
            || definition.AttributeDefinitionId == Guid.Empty
            || definition.VersionId == Guid.Empty
            || !IsHash(definition.ContentHash)
            || definition.FreshnessMaxAgeMinutes is < 1 or > MaxFreshnessMinutes
            || !HasValidStringSettings(dataType, normalization, definition.MaxLength)
            || definition.Operators is null or { Count: 0 })
        {
            throw new RuleRejectedException(RuleRejection.InvalidAttributeMetadata, "attribute");
        }

        var operators = new HashSet<RuleOperator>();
        foreach (var name in definition.Operators)
        {
            if (!RuleVocabulary.TryOperator(name, out var op) || !operators.Add(op))
            {
                throw new RuleRejectedException(RuleRejection.InvalidAttributeMetadata, "attribute");
            }
        }

        return new AttributeSpec(
            definition.AttributeDefinitionId,
            definition.VersionId,
            definition.ContentHash,
            scope,
            dataType,
            sensitivity,
            normalization,
            definition.FreshnessMaxAgeMinutes,
            definition.MaxLength,
            operators);
    }

    private static bool IsHash(string? hash) =>
        hash is { Length: HashLength } && hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>Length limits and normalisation apply to codes and text only, as in the catalog.</summary>
    private static bool HasValidStringSettings(RuleDataType dataType, RuleNormalization normalization, int? maxLength) =>
        dataType is RuleDataType.Code or RuleDataType.Text
            ? maxLength is >= 1 and <= MaxValueLength
            : maxLength is null && normalization == RuleNormalization.None;
}
