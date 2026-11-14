using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>
/// The ordered save used by attribute publishing and application activation (DL-064, DL-066). It relies on EF Core
/// restoring an entry's loaded values when the entry is marked Unchanged; if an upgrade changes that, these tests fail
/// instead of approvals silently saving the wrong values. No database is opened: the save step is recorded.
/// </summary>
public sealed class OrderedSaveTests
{
    [Fact]
    public async Task OtherChangesSaveFirstThenEachVersionWithItsDecidedValuesAndTheCurrentOneLast()
    {
        await using var db = new ProbeContext();
        var request = new Request { Id = 1, State = "Proposed" };
        var old = new Version { Id = 10, State = "Active" };
        var draft = new Version { Id = 11, State = "Draft" };
        db.AttachRange(request, old, draft);

        request.State = "Approved";
        draft.State = "Active";
        old.State = "Superseded";

        var steps = new List<string>();
        await db.SaveVersionsInOrderAsync(draft, _ =>
        {
            steps.Add(string.Join(" ", db.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Modified)
                .Select(e => e.Entity switch
                {
                    Request r => $"request={r.State}",
                    Version v => $"v{v.Id}={v.State}",
                    _ => "?",
                })
                .Order(StringComparer.Ordinal)));
            db.ChangeTracker.AcceptAllChanges();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        steps.ShouldBe(["request=Approved", "v10=Superseded", "v11=Active"]);
        (draft.State, old.State).ShouldBe(("Active", "Superseded"));
    }

    [Fact]
    public async Task MarkingAnEntryUnchangedRestoresItsLoadedValues()
    {
        await using var db = new ProbeContext();
        var version = new Version { Id = 1, State = "Draft" };
        db.Attach(version);
        version.State = "Active";
        db.ChangeTracker.DetectChanges();
        db.Entry(version).State = EntityState.Unchanged;
        version.State.ShouldBe("Draft", "OrderedSave copies the decided values because EF Core restores the loaded ones");
        await Task.CompletedTask;
    }

    private sealed class Request
    {
        public int Id { get; set; }

        public string State { get; set; } = string.Empty;
    }

    private sealed class Version
    {
        public int Id { get; set; }

        public string State { get; set; } = string.Empty;
    }

    private sealed class ProbeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlServer("Server=never-opened;Database=none;Encrypt=true");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Request>();
            modelBuilder.Entity<Version>();
        }
    }
}
