using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IAM.Modules.Policy.Domain.Rules;

/// <summary>Three-valued result of a condition or rule (DL-009).</summary>
public enum RuleTruth
{
    False = 0,
    True = 1,

    /// <summary>The data cannot settle it. Unknown never counts as a match and never falls through to a lower rule.</summary>
    Unknown = 2,
}

/// <summary>
/// Which rule applies. This selects a route; it grants and approves nothing. Every outcome except
/// <see cref="Matched"/> is a hold (IAM-WF-016, IAM-WF-018; CLAUDE.md rule 3).
/// </summary>
public enum RuleOutcome
{
    Matched = 1,
    HoldNoMatch = 2,
    HoldAmbiguous = 3,
    HoldMissingOrStaleData = 4,
    HoldBudgetExceeded = 5,
}

/// <summary>One condition's result. <see cref="Value"/> is null unless the attribute is Public or Internal and the value is present.</summary>
public sealed record ConditionEvidence(string Path, RuleSubject Subject, Guid AttributeId, string Test, RuleTruth Truth, AttributeValueState State, string? Value);

public sealed record RuleEvidence(Guid RuleId, int Priority, RuleTruth Truth, IReadOnlyList<ConditionEvidence> Conditions);

/// <summary>
/// What auditors need to reconstruct the decision without unnecessary sensitive values (IAM-WF-020). Sensitive values are
/// left out entirely, not hashed: hashes of short codes are easy to guess.
/// </summary>
public sealed record EvaluationEvidence(
    Guid PolicyVersionId,
    string PolicyHash,
    DateTimeOffset AsOfUtc,
    RuleOutcome Outcome,
    Guid? MatchedRuleId,
    IReadOnlyList<AttributeSpec> Attributes,
    IReadOnlyList<RuleEvidence> Rules)
{
    /// <summary>Canonical JSON: fixed property order and formats, so a replay gives byte-identical evidence (IAM-WF-022).</summary>
    public string ToCanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("policyVersionId", PolicyVersionId.ToString("D"));
            writer.WriteString("policyHash", PolicyHash);
            writer.WriteString("asOfUtc", AsOfUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("outcome", Outcome.ToString());
            if (MatchedRuleId is { } matched)
            {
                writer.WriteString("matchedRuleId", matched.ToString("D"));
            }

            writer.WriteStartArray("attributes");
            foreach (var attribute in Attributes)
            {
                writer.WriteStartObject();
                writer.WriteString("attributeId", attribute.AttributeId.ToString("D"));
                writer.WriteString("versionId", attribute.VersionId.ToString("D"));
                writer.WriteString("contentHash", attribute.ContentHash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("rules");
            foreach (var rule in Rules)
            {
                writer.WriteStartObject();
                writer.WriteString("ruleId", rule.RuleId.ToString("D"));
                writer.WriteNumber("priority", rule.Priority);
                writer.WriteString("truth", rule.Truth.ToString());
                writer.WriteStartArray("conditions");
                foreach (var condition in rule.Conditions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", condition.Path);
                    writer.WriteString("subject", condition.Subject.ToString());
                    writer.WriteString("attributeId", condition.AttributeId.ToString("D"));
                    writer.WriteString("test", condition.Test);
                    writer.WriteString("truth", condition.Truth.ToString());
                    writer.WriteString("state", condition.State.ToString());
                    if (condition.Value is not null)
                    {
                        writer.WriteString("value", condition.Value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public sealed record EvaluationResult(RuleOutcome Outcome, Guid? MatchedRuleId, EvaluationEvidence Evidence);

/// <summary>
/// Evaluates a compiled policy against a snapshot at an explicit time. A pure function: no clock, I/O or randomness, so
/// the same inputs always give the same result. Proof of concept (P0-008), not production-ready: nothing stores or calls
/// it yet, and precedence and limits still need the stakeholder confirmation recorded in DL-008 and DL-011.
/// <para>
/// Precedence is priority only (DL-011). Rules are checked from the highest priority down; at each priority level:
/// two or more true is ambiguous; any unknown is a hold, even beside a true rule, because the unknown one might also
/// match; one true is the match; all false moves to the next level. Nothing left is no match.
/// </para>
/// </summary>
public static class PolicyEvaluator
{
    public static EvaluationResult Evaluate(CompiledPolicy policy, AttributeSnapshot snapshot, DateTimeOffset asOf) =>
        Evaluate(policy, snapshot, asOf, RuleLimits.MaxEvaluationSteps);

    /// <summary>Internal: the budget is a fixed limit, not something a caller may widen, and it is part of what makes replay identical.</summary>
    internal static EvaluationResult Evaluate(CompiledPolicy policy, AttributeSnapshot snapshot, DateTimeOffset asOf, int maxSteps)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        var asOfUtc = asOf.ToUniversalTime();
        var run = new Run(snapshot, asOfUtc, maxSteps);
        var evidence = new List<RuleEvidence>();

        var eligible = policy.Rules.Where(r => IsEffective(r, asOfUtc));
        foreach (var level in eligible.GroupBy(r => r.Priority))
        {
            var matches = new List<Guid>();
            var unknown = false;
            foreach (var rule in level)
            {
                var conditions = new List<ConditionEvidence>();
                var truth = run.Visit(rule.Root, conditions);
                evidence.Add(new RuleEvidence(rule.RuleId, rule.Priority, run.OverBudget ? RuleTruth.Unknown : truth, conditions));
                if (run.OverBudget)
                {
                    return Result(policy, asOfUtc, RuleOutcome.HoldBudgetExceeded, null, evidence);
                }

                if (truth == RuleTruth.True)
                {
                    matches.Add(rule.RuleId);
                }
                else if (truth == RuleTruth.Unknown)
                {
                    unknown = true;
                }
            }

            if (matches.Count > 1)
            {
                return Result(policy, asOfUtc, RuleOutcome.HoldAmbiguous, null, evidence);
            }

            if (unknown)
            {
                return Result(policy, asOfUtc, RuleOutcome.HoldMissingOrStaleData, null, evidence);
            }

            if (matches.Count == 1)
            {
                return Result(policy, asOfUtc, RuleOutcome.Matched, matches[0], evidence);
            }
        }

        return Result(policy, asOfUtc, RuleOutcome.HoldNoMatch, null, evidence);
    }

    private static bool IsEffective(CompiledRule rule, DateTimeOffset asOfUtc) =>
        (rule.EffectiveFromUtc is not { } from || asOfUtc >= from) && (rule.EffectiveToUtc is not { } to || asOfUtc < to);

    private static EvaluationResult Result(CompiledPolicy policy, DateTimeOffset asOfUtc, RuleOutcome outcome, Guid? matched, List<RuleEvidence> rules) =>
        new(outcome, matched, new EvaluationEvidence(policy.VersionId, policy.ContentHash, asOfUtc, outcome, matched, policy.Attributes, rules));

    /// <summary>
    /// Compares a known value with a condition. Only a fresh, valid value can be compared: on anything else the answer is
    /// unknown, including NotEquals and NotIn, so missing data never passes as "not equal". The one exception is a null
    /// check on a value the source has recently confirmed is absent.
    /// </summary>
    private static RuleTruth Test(CompiledCondition condition, AttributeValueState state, TypedValue? value)
    {
        if (state == AttributeValueState.Empty)
        {
            return condition.Operator switch
            {
                RuleOperator.IsEmpty => RuleTruth.True,
                RuleOperator.IsNotEmpty => RuleTruth.False,
                _ => RuleTruth.Unknown,
            };
        }

        if (state != AttributeValueState.Present || value is null)
        {
            return RuleTruth.Unknown;
        }

        var result = condition.Operator switch
        {
            RuleOperator.IsEmpty => false,
            RuleOperator.IsNotEmpty => true,
            RuleOperator.Equals or RuleOperator.In => condition.LiteralSet.Contains(value.Canonical),
            RuleOperator.NotEquals or RuleOperator.NotIn => !condition.LiteralSet.Contains(value.Canonical),
            RuleOperator.GreaterThan => value.Comparable.CompareTo(condition.Literals[0].Comparable) > 0,
            RuleOperator.GreaterThanOrEqual => value.Comparable.CompareTo(condition.Literals[0].Comparable) >= 0,
            RuleOperator.LessThan => value.Comparable.CompareTo(condition.Literals[0].Comparable) < 0,
            RuleOperator.LessThanOrEqual => value.Comparable.CompareTo(condition.Literals[0].Comparable) <= 0,
            _ => (bool?)null,
        };
        return result switch
        {
            true => RuleTruth.True,
            false => RuleTruth.False,
            null => RuleTruth.Unknown,
        };
    }

    private static string? EvidenceValue(AttributeSpec attribute, TypedValue? value) =>
        attribute.Sensitivity is RuleSensitivity.Public or RuleSensitivity.Internal ? value?.Canonical : null;

    private sealed class Run(AttributeSnapshot snapshot, DateTimeOffset asOfUtc, int maxSteps)
    {
        private readonly Dictionary<(RuleSubject Subject, Guid AttributeId), (AttributeValueState State, TypedValue? Value)> _reads = [];
        private int _steps;

        public bool OverBudget { get; private set; }

        /// <summary>
        /// Every child is visited even when the answer is already settled, so the evidence and the step count do not
        /// depend on the order of the children.
        /// </summary>
        public RuleTruth Visit(CompiledNode node, List<ConditionEvidence> evidence)
        {
            _steps++;
            if (_steps > maxSteps)
            {
                OverBudget = true;
                return RuleTruth.Unknown;
            }

            switch (node)
            {
                case CompiledGroup group:
                    return VisitGroup(group, evidence);
                case CompiledCondition condition:
                    {
                        var state = Read(condition.Subject, condition.Attribute, out var value);
                        var truth = Test(condition, state, value);
                        evidence.Add(new ConditionEvidence(condition.Path, condition.Subject, condition.Attribute.AttributeId, condition.Operator.ToString(), truth, state, EvidenceValue(condition.Attribute, value)));
                        return truth;
                    }

                case CompiledWildcard wildcard:
                    {
                        // True only for a fresh, valid value: a missing attribute must not reach a broad rule (IAM-WF-018).
                        var state = Read(wildcard.Subject, wildcard.Attribute, out var value);
                        var truth = state == AttributeValueState.Present ? RuleTruth.True : RuleTruth.Unknown;
                        evidence.Add(new ConditionEvidence(wildcard.Path, wildcard.Subject, wildcard.Attribute.AttributeId, "Wildcard", truth, state, EvidenceValue(wildcard.Attribute, value)));
                        return truth;
                    }

                default:
                    return RuleTruth.Unknown;
            }
        }

        /// <summary>One read per attribute per evaluation: every rule testing it sees the same answer, and it is parsed once.</summary>
        private AttributeValueState Read(RuleSubject subject, AttributeSpec attribute, out TypedValue? value)
        {
            var key = (subject, attribute.AttributeId);
            if (!_reads.TryGetValue(key, out var read))
            {
                var state = snapshot.Read(subject, attribute, asOfUtc, out var parsed);
                read = (state, parsed);
                _reads[key] = read;
            }

            value = read.Value;
            return read.State;
        }

        private RuleTruth VisitGroup(CompiledGroup group, List<ConditionEvidence> evidence)
        {
            var anyTrue = false;
            var anyFalse = false;
            var anyUnknown = false;
            foreach (var child in group.Children)
            {
                var truth = Visit(child, evidence);
                if (OverBudget)
                {
                    return RuleTruth.Unknown;
                }

                anyTrue |= truth == RuleTruth.True;
                anyFalse |= truth == RuleTruth.False;
                anyUnknown |= truth == RuleTruth.Unknown;
            }

            // Kleene logic: a value that cannot change the answer does not matter (false AND unknown is false).
            return group.Operator == RuleGroupOperator.And
                ? anyFalse ? RuleTruth.False : anyUnknown ? RuleTruth.Unknown : RuleTruth.True
                : anyTrue ? RuleTruth.True : anyUnknown ? RuleTruth.Unknown : RuleTruth.False;
        }
    }
}
