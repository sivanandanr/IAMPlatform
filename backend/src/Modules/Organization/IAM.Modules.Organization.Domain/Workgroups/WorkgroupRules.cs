using System.Text.RegularExpressions;
using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Organization.Domain.Workgroups;

/// <summary>Where a member list version stands (DL-068). Only an approved request makes a draft active.</summary>
public enum MemberVersionState
{
    Draft = 1,
    Active = 2,
    Superseded = 3,
}

/// <summary>Why a workgroup change was refused. Codes only, never people or content.</summary>
public enum WorkgroupRefusal
{
    NotAllowed = 1,
    NoDraft = 2,
    DraftChanged = 3,
    MakerCannotDecide = 4,
    MemberCannotDecide = 5,
    NotProposed = 6,
    NotMaker = 7,
    InvalidReason = 8,
    InvalidRecord = 9,
    AlreadyPending = 10,
    DraftExists = 11,
    MakerNoLongerAllowed = 12,
}

public sealed class WorkgroupRefusedException : Exception
{
    public WorkgroupRefusedException()
    {
    }

    public WorkgroupRefusedException(string message)
        : base(message)
    {
    }

    public WorkgroupRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkgroupRefusedException(WorkgroupRefusal refusal, Exception? innerException = null)
        : base($"Workgroup change refused: {refusal}.", innerException)
    {
        Refusal = refusal;
    }

    public WorkgroupRefusal? Refusal { get; }
}

/// <summary>Workgroup input was invalid. The message names the field and rule only, never the value.</summary>
public sealed class WorkgroupRejectedException : Exception
{
    public WorkgroupRejectedException()
    {
    }

    public WorkgroupRejectedException(string message)
        : base(message)
    {
    }

    public WorkgroupRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkgroupRejectedException(string field, string rule, Exception? innerException = null)
        : base($"Workgroup input rejected: {field} {rule}.", innerException)
    {
        Field = field;
    }

    public string? Field { get; }
}

/// <summary>Input rules for workgroups (DL-068).</summary>
public static partial class WorkgroupRules
{
    public const int KeyMaxLength = 64;
    public const int DisplayNameMaxLength = 200;
    public const int MaxMembers = 256;
    public const int HashLength = 64;

    /// <summary>The built-in fallback workgroup (DL-057b, DL-068c): can't be removed; empty until members are approved.</summary>
    public const string FallbackKey = "iam_managers";

    /// <summary>The built-in fallback workgroup's fixed id, seeded by the Workgroups migrations.</summary>
    public static readonly Guid FallbackId = new("0193a1f0-0000-7000-8000-0000000000f1");

    /// <summary>A key: 2-64 lower-case letters, digits or underscores, starting with a letter. Never changes once stored.</summary>
    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static void ValidateKey(string? key, string field)
    {
        if (!IsKey(key))
        {
            throw new WorkgroupRejectedException(field, "must be 2-64 lower-case letters, digits or underscores, starting with a letter");
        }
    }

    public static void ValidateDisplayName(string? name)
    {
        if (!SecondPersonText.IsText(name, DisplayNameMaxLength))
        {
            throw new WorkgroupRejectedException("DisplayName", $"must be visible text of at most {DisplayNameMaxLength} characters");
        }
    }

    /// <summary>Members are people (sign-in ids), each once, at most 256. An empty list is allowed: nobody can act, so levels hold.</summary>
    public static IReadOnlyList<string> ValidateMembers(IReadOnlyList<string?>? members)
    {
        if (members is null || members.Count > MaxMembers)
        {
            throw new WorkgroupRejectedException("Members", $"must be a list of at most {MaxMembers} people");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!SecondPersonText.IsPerson(member) || !seen.Add(member!))
            {
                throw new WorkgroupRejectedException("Members", "must name each person once, by their id");
            }
        }

        return [.. seen.Order(StringComparer.Ordinal)];
    }

    public static bool IsHash(string? hash) =>
        hash is { Length: HashLength } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyPattern();
}

/// <summary>Runs a shared second-person rule and reports its refusal as a workgroup refusal code.</summary>
internal static class WorkgroupSecondPerson
{
    internal static T Shared<T>(Func<T> rule)
    {
        try
        {
            return rule();
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new WorkgroupRefusedException(Map(ex.Refusal), ex);
        }
    }

    internal static void Shared(Action rule) => Shared(() =>
    {
        rule();
        return true;
    });

    private static WorkgroupRefusal Map(SecondPersonRefusal? refusal) => refusal switch
    {
        SecondPersonRefusal.NotProposed => WorkgroupRefusal.NotProposed,
        SecondPersonRefusal.MakerCannotDecide => WorkgroupRefusal.MakerCannotDecide,
        SecondPersonRefusal.NotMaker => WorkgroupRefusal.NotMaker,
        SecondPersonRefusal.InvalidReason => WorkgroupRefusal.InvalidReason,
        SecondPersonRefusal.InvalidPerson => WorkgroupRefusal.NotAllowed,
        _ => WorkgroupRefusal.InvalidRecord,
    };
}
