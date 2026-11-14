using System.Globalization;

namespace IAM.BuildingBlocks.Abstractions.SecondPerson;

/// <summary>Where a second-person decision stands. Approved, rejected and withdrawn are final.</summary>
public enum SecondPersonState
{
    Proposed = 1,
    Approved = 2,
    Rejected = 3,
    Withdrawn = 4,
}

/// <summary>Why a second-person step was refused. Codes only, never people or text.</summary>
public enum SecondPersonRefusal
{
    InvalidPerson = 1,
    InvalidReason = 2,
    MakerCannotDecide = 3,
    NotProposed = 4,
    NotMaker = 5,
    InvalidRecord = 6,
}

public sealed class SecondPersonRefusedException : Exception
{
    public SecondPersonRefusedException()
    {
    }

    public SecondPersonRefusedException(string message)
        : base(message)
    {
    }

    public SecondPersonRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SecondPersonRefusedException(SecondPersonRefusal refusal)
        : base($"Second-person step refused: {refusal}.")
    {
        Refusal = refusal;
    }

    public SecondPersonRefusal? Refusal { get; }
}

/// <summary>Text rules every maker-checker record shares: who someone is, and why they decided.</summary>
public static class SecondPersonText
{
    /// <summary>The same bound as every stored actor (ADR-0017).</summary>
    public const int MaxPersonLength = 200;

    public const int MaxReasonLength = 1000;

    /// <summary>An opaque id from the identity provider: printable, no surrounding spaces, bounded.</summary>
    public static bool IsPerson(string? person) =>
        person is { Length: > 0 and <= MaxPersonLength }
        && !char.IsWhiteSpace(person[0]) && !char.IsWhiteSpace(person[^1])
        && person.All(c => !char.IsControl(c) && !IsFormatOrSeparator(c) && !char.IsSurrogate(c));

    /// <summary>Visible text of at most <paramref name="maxLength"/> characters, no control, format or separator characters.</summary>
    public static bool IsText(string? text, int maxLength) =>
        text is not null && text.Length <= maxLength && IsAcceptableText(text);

    public static bool IsReason(string? reason) => IsText(reason, MaxReasonLength);

    private static bool IsAcceptableText(string text) =>
        text.Any(c => !char.IsWhiteSpace(c) && !IsFormatOrSeparator(c))
        && text.All(c => !char.IsControl(c) && !IsFormatOrSeparator(c) || c == (char)0x200C)
        && HasNoLoneSurrogate(text);

    /// <summary>Format characters (such as bidirectional controls) and line or paragraph separators.</summary>
    private static bool IsFormatOrSeparator(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static bool HasNoLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The shared second-person approval (spec 12.2, DL-016, DL-059b, DL-064a): one person proposes with a reason, a different
/// person decides, only the maker can withdraw, and a decided record never changes. Each module stores its own records and
/// adds its own checks (who may propose or decide, what the change does); these rules are the same everywhere.
/// </summary>
public sealed class SecondPersonDecision
{
    private SecondPersonDecision(string maker, string reason, DateTimeOffset proposedAtUtc)
    {
        Maker = maker;
        Reason = reason;
        ProposedAtUtc = proposedAtUtc;
        State = SecondPersonState.Proposed;
    }

    public string Maker { get; }

    public string Reason { get; }

    public DateTimeOffset ProposedAtUtc { get; }

    public SecondPersonState State { get; private set; }

    /// <summary>Who decided: the checker, or the maker for a withdrawal.</summary>
    public string? Checker { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    public static SecondPersonDecision Propose(string maker, string? reason, DateTimeOffset now)
    {
        if (!SecondPersonText.IsPerson(maker))
        {
            throw new SecondPersonRefusedException(SecondPersonRefusal.InvalidPerson);
        }

        return !SecondPersonText.IsReason(reason)
            ? throw new SecondPersonRefusedException(SecondPersonRefusal.InvalidReason)
            : new SecondPersonDecision(maker, reason!, now.ToUniversalTime());
    }

    /// <summary>
    /// Rebuilds a stored record and checks its shape: a proposal has no decision; a withdrawal was decided by the maker; a
    /// rejection by someone else, with a reason; an approval by someone else. Anything else is refused as damaged.
    /// </summary>
    public static SecondPersonDecision Restore(
        string maker,
        string reason,
        DateTimeOffset proposedAtUtc,
        SecondPersonState state,
        string? checker,
        string? decisionReason,
        DateTimeOffset? decidedAtUtc)
    {
        var valid = SecondPersonText.IsPerson(maker)
            && SecondPersonText.IsReason(reason)
            && Enum.IsDefined(state)
            && (decisionReason is null || SecondPersonText.IsReason(decisionReason))
            && state switch
            {
                SecondPersonState.Proposed => checker is null && decisionReason is null && decidedAtUtc is null,
                SecondPersonState.Withdrawn => string.Equals(checker, maker, StringComparison.Ordinal) && decidedAtUtc is not null,
                SecondPersonState.Rejected => SecondPersonText.IsPerson(checker) && !string.Equals(checker, maker, StringComparison.Ordinal) && decisionReason is not null && decidedAtUtc is not null,
                SecondPersonState.Approved => SecondPersonText.IsPerson(checker) && !string.Equals(checker, maker, StringComparison.Ordinal) && decidedAtUtc is not null,
                _ => false,
            };
        return !valid
            ? throw new SecondPersonRefusedException(SecondPersonRefusal.InvalidRecord)
            : new SecondPersonDecision(maker, reason, proposedAtUtc)
            {
                State = state,
                Checker = checker,
                DecisionReason = decisionReason,
                DecidedAtUtc = decidedAtUtc,
            };
    }

    /// <summary>Refuses unless the record is still proposed and <paramref name="checker"/> is a valid person other than the maker.</summary>
    public void RequireDecider(string? checker)
    {
        RequireProposed();
        if (string.Equals(checker, Maker, StringComparison.Ordinal))
        {
            throw new SecondPersonRefusedException(SecondPersonRefusal.MakerCannotDecide);
        }

        if (!SecondPersonText.IsPerson(checker))
        {
            throw new SecondPersonRefusedException(SecondPersonRefusal.InvalidPerson);
        }
    }

    /// <summary>An optional reason, if given, must be valid text.</summary>
    public static void RequireOptionalReason(string? reason)
    {
        if (reason is not null && !SecondPersonText.IsReason(reason))
        {
            throw new SecondPersonRefusedException(SecondPersonRefusal.InvalidReason);
        }
    }

    public void Approve(string checker, string? reason, DateTimeOffset now)
    {
        RequireDecider(checker);
        RequireOptionalReason(reason);
        Decide(SecondPersonState.Approved, checker, reason, now);
    }

    /// <summary>A rejection must say why.</summary>
    public void Reject(string checker, string? reason, DateTimeOffset now)
    {
        RequireDecider(checker);
        if (!SecondPersonText.IsReason(reason))
        {
            throw new SecondPersonRefusedException(SecondPersonRefusal.InvalidReason);
        }

        Decide(SecondPersonState.Rejected, checker, reason, now);
    }

    /// <summary>Only the maker can withdraw their own proposal.</summary>
    public void Withdraw(string actor, string? reason, DateTimeOffset now)
    {
        RequireProposed();
        if (!string.Equals(actor, Maker, StringComparison.Ordinal))
        {
            throw new SecondPersonRefusedException(SecondPersonRefusal.NotMaker);
        }

        RequireOptionalReason(reason);
        Decide(SecondPersonState.Withdrawn, actor, reason, now);
    }

    public void RequireProposed()
    {
        if (State != SecondPersonState.Proposed)
        {
            throw new SecondPersonRefusedException(SecondPersonRefusal.NotProposed);
        }
    }

    private void Decide(SecondPersonState state, string by, string? reason, DateTimeOffset now)
    {
        State = state;
        Checker = by;
        DecisionReason = reason;
        DecidedAtUtc = now.ToUniversalTime();
    }
}
