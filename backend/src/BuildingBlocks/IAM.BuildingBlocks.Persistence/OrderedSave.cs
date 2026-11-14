using Microsoft.EntityFrameworkCore;

namespace IAM.BuildingBlocks.Persistence;

/// <summary>
/// Saves a second-person decision and the versions it changes in a fixed order (DL-064, DL-066): first everything except
/// those versions (the decided request, which the database must see as approved before a version may change state), then
/// each changed version one at a time, with <paramref name="last"/> (the one becoming current) saved last, so a "one current
/// version" unique index never sees two. All in the caller's transaction.
/// </summary>
/// <remarks>
/// Marking a Modified entry Unchanged makes EF Core restore its loaded values, so the decided values are copied first and
/// put back before each step. <c>OrderedSaveTests</c> fails if an EF Core upgrade changes that behaviour.
/// </remarks>
public static class OrderedSave
{
    public static async Task SaveVersionsInOrderAsync<TVersion>(this DbContext db, TVersion last, Func<CancellationToken, Task> save, CancellationToken cancellationToken)
        where TVersion : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(last);
        ArgumentNullException.ThrowIfNull(save);
        var decided = db.ChangeTracker.Entries<TVersion>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (Entry: e, Values: e.CurrentValues.Clone()))
            .ToList();
        foreach (var (entry, _) in decided)
        {
            entry.State = EntityState.Unchanged;
        }

        await save(cancellationToken).ConfigureAwait(false);
        foreach (var (entry, values) in decided.OrderBy(d => ReferenceEquals(d.Entry.Entity, last) ? 1 : 0))
        {
            entry.CurrentValues.SetValues(values);
            await save(cancellationToken).ConfigureAwait(false);
        }
    }
}
