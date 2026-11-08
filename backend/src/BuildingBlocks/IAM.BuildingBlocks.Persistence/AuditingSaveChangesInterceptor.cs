using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IAM.BuildingBlocks.Persistence;

/// <summary>
/// Sets the audit and concurrency columns from the server-side actor and clock (ADR-0017):
/// inserts get Created*/Modified* and Version 1; updates get Modified* and Version + 1, and any attempt to change
/// Created* is discarded. Saving without an actor fails, and so does inserting an empty Guid key.
/// </summary>
public sealed class AuditingSaveChangesInterceptor(TimeProvider clock, IAuditActorAccessor actorAccessor) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Where(e => e.Metadata.FindProperty(AuditColumns.Version) is not null)
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var actor = actorAccessor.CurrentActor;
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > AuditColumns.ActorMaxLength)
        {
            throw new InvalidOperationException("Cannot save changes without a valid server-side actor (ADR-0017).");
        }

        var now = UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                foreach (var key in entry.Metadata.FindPrimaryKey()?.Properties ?? [])
                {
                    if (key.ClrType == typeof(Guid) && entry.Property(key.Name).CurrentValue is Guid id && id == Guid.Empty)
                    {
                        throw new InvalidOperationException(
                            $"{entry.Metadata.DisplayName()} has an empty Id. Generate it in the application with Guid.CreateVersion7() (ADR-0016).");
                    }
                }

                entry.Property(AuditColumns.CreatedAtUtc).CurrentValue = now;
                entry.Property(AuditColumns.CreatedBy).CurrentValue = actor;
                entry.Property(AuditColumns.ModifiedAtUtc).CurrentValue = now;
                entry.Property(AuditColumns.ModifiedBy).CurrentValue = actor;
                entry.Property(AuditColumns.Version).CurrentValue = 1L;
            }
            else
            {
                foreach (var name in new[] { AuditColumns.CreatedAtUtc, AuditColumns.CreatedBy })
                {
                    var created = entry.Property(name);
                    created.CurrentValue = created.OriginalValue;
                    created.IsModified = false;
                }

                var version = entry.Property(AuditColumns.Version);
                version.CurrentValue = (long)version.OriginalValue! + 1;
                entry.Property(AuditColumns.ModifiedAtUtc).CurrentValue = now;
                entry.Property(AuditColumns.ModifiedBy).CurrentValue = actor;
            }
        }
    }
}
