namespace IAM.Modules.Identity.Contracts;

/// <summary>One person attribute value, with when the source last confirmed it. Values may be sensitive: never logged.</summary>
public sealed record PersonAttributeValue(Guid AttributeId, string Key, string? Value, DateTimeOffset SynchronizedAtUtc)
{
    public override string ToString() => $"{nameof(PersonAttributeValue)} {Key}";
}

/// <summary>
/// A person's attribute values from the authoritative source (HR, DL-090g). Phase 0 has no source: the implementation returns
/// nothing, so anything that depends on a person's attributes (a matrix condition, a naming rule) fails closed. Not
/// production-ready (CLAUDE.md rule 10).
/// </summary>
public interface IPersonAttributeSource
{
    /// <summary>The person's published attribute values, or an empty list when the source has nothing.</summary>
    Task<IReadOnlyList<PersonAttributeValue>> FindAsync(string person, CancellationToken cancellationToken);
}
