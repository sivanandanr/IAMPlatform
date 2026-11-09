namespace IAM.Modules.Policy.Domain.Rules;

/// <summary>Two rules at the same priority that might both match the same person. Publishing must be blocked until resolved (IAM-WF-016).</summary>
public sealed record RuleOverlap(Guid FirstRuleId, Guid SecondRuleId, int Priority);

/// <summary>What the pre-publication check found. Clean only if nothing was found and nothing was cut off.</summary>
public sealed record RuleAnalysis(IReadOnlyList<RuleOverlap> Overlaps, IReadOnlyList<Guid> NeverTrueRuleIds, bool Truncated)
{
    public bool IsClean => Overlaps.Count == 0 && NeverTrueRuleIds.Count == 0 && !Truncated;
}

/// <summary>
/// Pre-publication check (IAM-WF-016). Two rules at the same priority overlap unless they provably cannot both be true:
/// their effective periods do not meet, or both constrain an attribute, through AND only, to values or ranges that share
/// nothing. A rule whose own conditions contradict each other (X = 1 AND X = 2) is reported as never true. It errs towards
/// flagging: OR groups are not analysed, and a range is not narrowed to the whole numbers inside it, so a harmless pair
/// can be reported. It never claims a pair is disjoint without proof. The evaluator still holds if an ambiguous match
/// occurs at run time. It cannot tell whether two overlapping rules disagree, because rules have no route yet.
/// </summary>
public static class RuleOverlapAnalyzer
{
    public static RuleAnalysis Analyze(CompiledPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var overlaps = new List<RuleOverlap>();
        var neverTrue = new List<Guid>();
        var truncated = false;
        foreach (var level in policy.Rules.GroupBy(r => r.Priority))
        {
            var rules = level.Select(rule => (Rule: rule, Constraints: Constraints(rule.Root))).ToList();
            neverTrue.AddRange(rules.Where(r => r.Constraints.Values.Any(c => c.IsImpossible)).Select(r => r.Rule.RuleId));
            for (var i = 0; i < rules.Count && !truncated; i++)
            {
                for (var j = i + 1; j < rules.Count; j++)
                {
                    if (!PeriodsMeet(rules[i].Rule, rules[j].Rule) || Disjoint(rules[i].Constraints, rules[j].Constraints))
                    {
                        continue;
                    }

                    if (overlaps.Count == RuleLimits.MaxOverlapsReported)
                    {
                        truncated = true;
                        break;
                    }

                    overlaps.Add(new RuleOverlap(rules[i].Rule.RuleId, rules[j].Rule.RuleId, level.Key));
                }
            }
        }

        return new RuleAnalysis(overlaps, neverTrue, truncated);
    }

    private static bool PeriodsMeet(CompiledRule first, CompiledRule second) =>
        (first.EffectiveToUtc is not { } firstEnd || second.EffectiveFromUtc is not { } secondStart || secondStart < firstEnd)
        && (second.EffectiveToUtc is not { } secondEnd || first.EffectiveFromUtc is not { } firstStart || firstStart < secondEnd);

    private static bool Disjoint(Dictionary<(RuleSubject Subject, Guid AttributeId), Constraint> first, Dictionary<(RuleSubject Subject, Guid AttributeId), Constraint> second)
    {
        foreach (var (key, constraint) in first)
        {
            if (second.TryGetValue(key, out var other) && !constraint.CanShareValueWith(other))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What each attribute must satisfy for the rule to be true, from conditions reached only through AND. An OR contributes nothing.</summary>
    private static Dictionary<(RuleSubject Subject, Guid AttributeId), Constraint> Constraints(CompiledNode root)
    {
        var constraints = new Dictionary<(RuleSubject Subject, Guid AttributeId), Constraint>();
        Collect(root, constraints);
        return constraints;
    }

    private static void Collect(CompiledNode node, Dictionary<(RuleSubject Subject, Guid AttributeId), Constraint> constraints)
    {
        switch (node)
        {
            case CompiledGroup { Operator: RuleGroupOperator.And } group:
                foreach (var child in group.Children)
                {
                    Collect(child, constraints);
                }

                break;
            case CompiledCondition condition:
                var key = (condition.Subject, condition.Attribute.AttributeId);
                if (!constraints.TryGetValue(key, out var constraint))
                {
                    constraint = new Constraint();
                    constraints[key] = constraint;
                }

                constraint.Apply(condition);
                break;
        }
    }

    /// <summary>The values one attribute may take: an allowed list (if any), excluded values, and a range.</summary>
    private sealed class Constraint
    {
        private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
        private Dictionary<string, IComparable>? _allowed;
        private IComparable? _lower;
        private bool _lowerInclusive;
        private IComparable? _upper;
        private bool _upperInclusive;

        /// <summary>True when no value can satisfy every condition on this attribute.</summary>
        public bool IsImpossible => RangeIsEmpty(_lower, _lowerInclusive, _upper, _upperInclusive) || (_allowed is not null && !_allowed.Any(v => Accepts(v.Key, v.Value)));

        public void Apply(CompiledCondition condition)
        {
            var literal = condition.Literals.Count > 0 ? condition.Literals[0].Comparable : null;
            switch (condition.Operator)
            {
                case RuleOperator.Equals or RuleOperator.In:
                    var values = condition.Literals.ToDictionary(l => l.Canonical, l => l.Comparable, StringComparer.Ordinal);
                    _allowed = _allowed is null ? values : _allowed.Where(v => values.ContainsKey(v.Key)).ToDictionary(StringComparer.Ordinal);
                    break;
                case RuleOperator.NotEquals or RuleOperator.NotIn:
                    _excluded.UnionWith(condition.LiteralSet);
                    break;
                case RuleOperator.GreaterThan:
                    TightenLower(literal!, inclusive: false);
                    break;
                case RuleOperator.GreaterThanOrEqual:
                    TightenLower(literal!, inclusive: true);
                    break;
                case RuleOperator.LessThan:
                    TightenUpper(literal!, inclusive: false);
                    break;
                case RuleOperator.LessThanOrEqual:
                    TightenUpper(literal!, inclusive: true);
                    break;
            }
        }

        /// <summary>False only with proof that no value satisfies both. Excluded values on a range are not used, which can only flag more.</summary>
        public bool CanShareValueWith(Constraint other)
        {
            if (_allowed is not null)
            {
                return _allowed.Any(v => Accepts(v.Key, v.Value) && other.Accepts(v.Key, v.Value));
            }

            if (other._allowed is not null)
            {
                return other._allowed.Any(v => other.Accepts(v.Key, v.Value) && Accepts(v.Key, v.Value));
            }

            var (lower, lowerInclusive) = Tighter(_lower, _lowerInclusive, other._lower, other._lowerInclusive, wantHigher: true);
            var (upper, upperInclusive) = Tighter(_upper, _upperInclusive, other._upper, other._upperInclusive, wantHigher: false);
            return !RangeIsEmpty(lower, lowerInclusive, upper, upperInclusive);
        }

        private static bool RangeIsEmpty(IComparable? lower, bool lowerInclusive, IComparable? upper, bool upperInclusive)
        {
            if (lower is null || upper is null)
            {
                return false;
            }

            var order = lower.CompareTo(upper);
            return order > 0 || (order == 0 && !(lowerInclusive && upperInclusive));
        }

        private static (IComparable? Bound, bool Inclusive) Tighter(IComparable? first, bool firstInclusive, IComparable? second, bool secondInclusive, bool wantHigher)
        {
            if (first is null)
            {
                return (second, secondInclusive);
            }

            if (second is null)
            {
                return (first, firstInclusive);
            }

            var order = first.CompareTo(second);
            return order == 0
                ? (first, firstInclusive && secondInclusive)
                : (order > 0) == wantHigher ? (first, firstInclusive) : (second, secondInclusive);
        }

        private bool Accepts(string canonical, IComparable value) =>
            (_allowed is null || _allowed.ContainsKey(canonical))
            && !_excluded.Contains(canonical)
            && (_lower is null || (_lowerInclusive ? value.CompareTo(_lower) >= 0 : value.CompareTo(_lower) > 0))
            && (_upper is null || (_upperInclusive ? value.CompareTo(_upper) <= 0 : value.CompareTo(_upper) < 0));

        private void TightenLower(IComparable bound, bool inclusive) =>
            (_lower, _lowerInclusive) = Tighter(_lower, _lowerInclusive, bound, inclusive, wantHigher: true);

        private void TightenUpper(IComparable bound, bool inclusive) =>
            (_upper, _upperInclusive) = Tighter(_upper, _upperInclusive, bound, inclusive, wantHigher: false);
    }
}
