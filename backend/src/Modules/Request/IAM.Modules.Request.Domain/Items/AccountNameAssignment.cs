using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Request.Domain.Items;

/// <summary>
/// A name given to a request item's new account by a workflow level (CLAUDE.md "Action-only fields", DL-092): which item,
/// which Action stage, who gave it, the name before (the naming rule's proposal or an earlier assignment, or none) and the
/// name now. Names only, never access, people or dates (rule 11). Append-only: the latest assignment is the account's name.
/// </summary>
public sealed class AccountNameAssignment
{
    private AccountNameAssignment()
    {
        Name = string.Empty;
        SetBy = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>The database's insertion order (DL-100): the latest name is the last one written, never by any host's clock.</summary>
    public long Seq { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid StageId { get; private set; }

    /// <summary>The name before this assignment; null when there was none.</summary>
    public string? PreviousName { get; private set; }

    public string Name { get; private set; }

    public string SetBy { get; private set; }

    public DateTimeOffset SetAtUtc { get; private set; }

    /// <summary>
    /// Records a name for an item that asks for a new account. The caller has checked that the stage is the item's, an open
    /// Action stage listing the account-name field, that <paramref name="setBy"/> may act on it, and that no live account has the name.
    /// </summary>
    public static AccountNameAssignment Give(RequestItem item, Guid stageId, string? previousName, string name, string setBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.NewAccountType is null)
        {
            throw new RequestRefusedException(RequestRefusal.NoNewAccount);
        }

        if (!RequestItem.IsAccountName(name))
        {
            throw new RequestRejectedException("Name", "must be 1-256 letters, digits, dots, underscores or hyphens, starting with a letter or digit");
        }

        if (stageId == Guid.Empty || !SecondPersonText.IsPerson(setBy) || (previousName is not null && !RequestItem.IsAccountName(previousName)))
        {
            throw new RequestRefusedException(RequestRefusal.InvalidRecord);
        }

        if (string.Equals(previousName, name, StringComparison.Ordinal))
        {
            throw new RequestRefusedException(RequestRefusal.NameUnchanged);
        }

        return new AccountNameAssignment
        {
            Id = Guid.CreateVersion7(),
            ItemId = item.Id,
            StageId = stageId,
            PreviousName = previousName,
            Name = name,
            SetBy = setBy,
            SetAtUtc = now,
        };
    }
}
