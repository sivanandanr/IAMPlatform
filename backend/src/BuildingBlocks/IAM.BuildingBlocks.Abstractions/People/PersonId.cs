using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.BuildingBlocks.Abstractions.People;

/// <summary>
/// A person, by the identity provider's sign-in id (<c>sub</c>), until a person directory replaces it (DL-059d, DL-069a).
/// Compared exactly. Only <see cref="Create"/> and <see cref="TryCreate"/> make one, so a value is always valid; the
/// default value is not, and every check refuses it. <see cref="ToString"/> never prints the id, so logging a value can't
/// expose a person.
/// </summary>
public readonly record struct PersonId
{
    private readonly string? _value;

    private PersonId(string value)
    {
        _value = value;
    }

    /// <summary>The sign-in id. Throws on the default value, which names nobody.</summary>
    public string Value => _value ?? throw new InvalidOperationException("The default PersonId names nobody.");

    /// <summary>False only for the default value.</summary>
    public bool IsValid => _value is not null;

    public static PersonId Create(string? value) =>
        TryCreate(value, out var person) ? person : throw new ArgumentException("Not a valid person id.", nameof(value));

    public static bool TryCreate(string? value, out PersonId person)
    {
        person = SecondPersonText.IsPerson(value) ? new PersonId(value!) : default;
        return person.IsValid;
    }

    public override string ToString() => nameof(PersonId);
}
