using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Application;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Persistence.Tests.Audit;
using IAM.Persistence.Tests.Probe;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// S1-07 append-only audit behaviour that must be identical on SQL Server and PostgreSQL (ADR-0010, ADR-0018):
/// the audit row commits and rolls back with the business change, a failed audit write takes the business change down
/// with it, runtime logins can only append (grant layer), and nobody, including the schema owner, can update or delete
/// a row through normal SQL (trigger layer). The two layers are asserted separately by their distinct errors.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class AuditParityTests(ParityDatabase db)
{
    private const string Action = "parity.item.created";
    private const string TriggerMessage = "append-only";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    private string Table => IsSqlServer ? "[audit].[AuditEvent]" : "audit.audit_event";

    private string HistoryTable => IsSqlServer ? "[audit].[__EFMigrationsHistory]" : "audit.__ef_migrations_history";

    private string Col(string pascal) => IsSqlServer ? $"[{pascal}]" : IamModelConventions.ToSnakeCase(pascal);

    // ---- Migration --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AuditMigrationsApplyAndReRunCleanly()
    {
        db.AuditFirstRunApplied.ShouldNotBeEmpty();
        db.AuditSecondRunPending.ShouldBeEmpty();

        await using var audit = TestSupport.CreateAudit(db.Provider, db.MigratorConnectionString);
        (await audit.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(audit.Database.GetMigrations());
    }

    // ---- Atomicity --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAuditRowCommitsWithTheBusinessChange()
    {
        var correlation = NewCorrelation();
        var item = NewItem();

        await using (var context = db.CreateProbe())
        await using (var tx = await context.Database.BeginTransactionAsync(Ct))
        {
            context.Items.Add(item);
            await context.SaveChangesAsync(Ct);
            await Writer().AppendAsync(tx.GetDbTransaction(), Record(correlation), Ct);
            await tx.CommitAsync(Ct);
        }

        (await CountAuditAsync(correlation)).ShouldBe(1);
        (await ItemExistsAsync(item.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task TheAuditRowRollsBackWithTheBusinessChange()
    {
        var correlation = NewCorrelation();
        var item = NewItem();

        await using (var context = db.CreateProbe())
        await using (var tx = await context.Database.BeginTransactionAsync(Ct))
        {
            context.Items.Add(item);
            await context.SaveChangesAsync(Ct);
            await Writer().AppendAsync(tx.GetDbTransaction(), Record(correlation), Ct);
            await tx.RollbackAsync(Ct);
        }

        (await CountAuditAsync(correlation)).ShouldBe(0);
        (await ItemExistsAsync(item.Id)).ShouldBeFalse();
    }

    /// <summary>A caller that catches the audit failure and commits anyway must not keep the business change.</summary>
    [Fact]
    public async Task ARejectedAuditRecordRollsBackTheBusinessChange()
    {
        var correlation = NewCorrelation();
        var bad = Record(correlation) with { Payload = [new AuditPayloadField("accessToken", "x", AuditFieldClass.Plain)] };
        await AssertFailedAuditTakesTheChangeDownAsync(correlation, tx => Writer().AppendAsync(tx, bad, Ct));
    }

    [Fact]
    public async Task ACancelledAuditWriteRollsBackTheBusinessChange()
    {
        var correlation = NewCorrelation();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await AssertFailedAuditTakesTheChangeDownAsync(correlation, tx => Writer().AppendAsync(tx, Record(correlation), cancelled.Token));
    }

    [Fact]
    public async Task NoActorMeansNoAuditRowAndNoBusinessChange()
    {
        var correlation = NewCorrelation();
        await AssertFailedAuditTakesTheChangeDownAsync(correlation, tx => Writer(actor: null).AppendAsync(tx, Record(correlation), Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACompletedTransactionIsRefused(bool commit)
    {
        var correlation = NewCorrelation();
        await using var context = db.CreateProbe();
        await using var tx = await context.Database.BeginTransactionAsync(Ct);
        var dbTransaction = tx.GetDbTransaction();
        if (commit)
        {
            await tx.CommitAsync(Ct);
        }
        else
        {
            await tx.RollbackAsync(Ct);
        }

        // Not disposed: the writer's own guard must refuse it, not an ObjectDisposedException.
        (await Should.ThrowAsync<InvalidOperationException>(() => Writer().AppendAsync(dbTransaction, Record(correlation), Ct)))
            .Message.ShouldContain("has already completed", Case.Sensitive);
        (await CountAuditAsync(correlation)).ShouldBe(0);
    }

    // ---- Stored content ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStoredRowIsMaskedAndMatchesItsRowHash()
    {
        var correlation = NewCorrelation();
        var record = Record(correlation) with
        {
            Reason = "line one\nline two",
            Payload =
            [
                new AuditPayloadField("company", "0100", AuditFieldClass.Plain),
                new AuditPayloadField("employeeNumber", "00012345", AuditFieldClass.Sensitive),
                new AuditPayloadField("displayName", "Zoë 漢字", AuditFieldClass.Plain),
            ],
        };

        AuditWriteReceipt receipt;
        await using (var context = db.CreateProbe())
        await using (var tx = await context.Database.BeginTransactionAsync(Ct))
        {
            receipt = await Writer().AppendAsync(tx.GetDbTransaction(), record, Ct);
            await tx.CommitAsync(Ct);
        }

        var stored = await ReadRowAsync(correlation);
        stored.Row.Id.ShouldBe(receipt.EventId);
        stored.Row.RecordedAtUtc.ShouldBe(receipt.RecordedAtUtc, "microsecond timestamps survive the round trip");
        stored.Row.PayloadCanonical.ShouldNotContain("00012345");
        stored.Row.PayloadCanonical.ShouldContain("\"company\":\"0100\"");
        stored.Row.ActorId.ShouldBe("parity-tester");
        stored.Row.Reason.ShouldBe("line one\nline two");
        stored.Row.RowHash.ShouldBe(receipt.RowHash);
        AuditEventValidator.ComputeRowHash(stored.Row).ShouldBe(stored.Row.RowHash, "the stored columns must hash to the stored row hash");
    }

    [Fact]
    public async Task SequenceNumbersIncreaseInWriteOrder()
    {
        var first = NewCorrelation();
        var second = NewCorrelation();
        foreach (var correlation in new[] { first, second })
        {
            await AppendCommittedAsync(correlation);
        }

        (await ReadRowAsync(second)).Seq.ShouldBeGreaterThan((await ReadRowAsync(first)).Seq);
    }

    // ---- Grant layer (permission errors only, never the trigger's) --------------------------------------------

    [Fact]
    public async Task ModuleLoginsCanAppendButNotReadChangeOrRemove()
    {
        await AppendCommittedAsync(NewCorrelation());
        await ShouldBePermissionDeniedAsync(db.ProbeAppConnectionString, $"SELECT COUNT(*) FROM {Table}");
        await ShouldBePermissionDeniedAsync(db.ProbeAppConnectionString, $"UPDATE {Table} SET {Col("Reason")} = 'x'");
        await ShouldBePermissionDeniedAsync(db.ProbeAppConnectionString, $"DELETE FROM {Table}");
        await ShouldBePermissionDeniedAsync(db.ProbeAppConnectionString, $"TRUNCATE TABLE {Table}");
        await ShouldBePermissionDeniedAsync(db.IdentityAppConnectionString, $"DELETE FROM {Table}");
    }

    [Fact]
    public async Task TheAuditModuleLoginCanReadButNotChangeOrRemove()
    {
        await AppendCommittedAsync(NewCorrelation());
        (await CountAllAsync()).ShouldBeGreaterThan(0);
        await ShouldBePermissionDeniedAsync(db.AuditAppConnectionString, $"UPDATE {Table} SET {Col("Reason")} = 'x'");
        await ShouldBePermissionDeniedAsync(db.AuditAppConnectionString, $"DELETE FROM {Table}");
        await ShouldBePermissionDeniedAsync(db.AuditAppConnectionString, $"TRUNCATE TABLE {Table}");
    }

    [Fact]
    public async Task NoRuntimeLoginCanTouchTheAuditMigrationHistory()
    {
        var insert = IsSqlServer
            ? $"INSERT INTO {HistoryTable} ([MigrationId], [ProductVersion]) VALUES ('99999999999999_Fake', 'x')"
            : $"INSERT INTO {HistoryTable} (\"MigrationId\", \"ProductVersion\") VALUES ('99999999999999_Fake', 'x')";
        foreach (var login in new[] { db.AuditAppConnectionString, db.ProbeAppConnectionString })
        {
            await ShouldBePermissionDeniedAsync(login, insert);
            await ShouldBePermissionDeniedAsync(login, $"SELECT COUNT(*) FROM {HistoryTable}");
        }
    }

    /// <summary>A writer cannot choose sequence numbers, so it cannot block later audit writes or forge ordering.</summary>
    [Fact]
    public async Task WritersCannotSupplySequenceNumbers()
    {
        await AppendCommittedAsync(NewCorrelation());
        var withoutSeq = RawInsert(includeSeq: false);
        var withSeq = RawInsert(includeSeq: true);

        await db.ExecuteAsync(db.ProbeAppConnectionString, withoutSeq, Ct); // control: the same insert without seq is allowed
        var ex = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.ProbeAppConnectionString, withSeq, Ct));
        ex.Message.ShouldMatch(IsSqlServer ? "(?i)IDENTITY_INSERT" : "(?i)permission denied");
        (await ScalarLongAsync(db.AuditAppConnectionString, $"SELECT COUNT(*) FROM {Table} WHERE {Col("Seq")} = 900000000")).ShouldBe(0);
    }

    [Fact]
    public async Task ModuleLoginsCannotAssumeTheWriterRole()
    {
        if (IsSqlServer)
        {
            // SQL Server has no SET ROLE; database roles cannot be assumed by a session.
            return;
        }

        await ShouldBePermissionDeniedAsync(db.ProbeAppConnectionString, "SET ROLE iam_audit_writer");
    }

    // ---- Trigger layer ----------------------------------------------------------------------------------------

    /// <summary>
    /// The migrator owns the schema, so grants do not bind it; the triggers do. (It could still drop or disable a
    /// trigger: that is a reviewed migration, and the residual risk is recorded in ADR-0018 / R-026.)
    /// </summary>
    [Fact]
    public async Task TriggersStopEvenTheSchemaOwnerFromChangingRows()
    {
        await AppendCommittedAsync(NewCorrelation());
        var before = await CountAllAsync();

        (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"UPDATE {Table} SET {Col("Reason")} = 'x'", Ct)))
            .Message.ShouldContain(TriggerMessage);
        (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"DELETE FROM {Table}", Ct)))
            .Message.ShouldContain(TriggerMessage);
        if (!IsSqlServer)
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE TABLE {Table}", Ct)))
                .Message.ShouldContain(TriggerMessage);
        }

        (await CountAllAsync()).ShouldBe(before);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private AuditWriter Writer(string? actor = "parity-tester") => new(
        TestSupport.ProviderFor(db.Provider),
        TimeProvider.System,
        new FixedActor(actor),
        new AuditActionCatalog([new AuditActionRegistration(Action)]));

    private static AuditEventRecord Record(string correlation) => AuditEventValidatorTests.ValidRecord() with
    {
        CorrelationId = correlation,
        TargetId = Guid.CreateVersion7().ToString(),
        OccurredAtUtc = DateTimeOffset.UtcNow,
    };

    private static string NewCorrelation() => "c-" + Guid.NewGuid().ToString("N");

    private static ProbeItem NewItem() => new()
    {
        Code = "a" + Guid.NewGuid().ToString("N")[..12],
        DisplayName = "audit parity",
        EffectiveAtUtc = DateTimeOffset.UtcNow,
    };

    private async Task AssertFailedAuditTakesTheChangeDownAsync(string correlation, Func<DbTransaction, Task> failingWrite)
    {
        var item = NewItem();
        await using (var context = db.CreateProbe())
        await using (var tx = await context.Database.BeginTransactionAsync(Ct))
        {
            context.Items.Add(item);
            await context.SaveChangesAsync(Ct);
            await Should.ThrowAsync<Exception>(() => failingWrite(tx.GetDbTransaction()));

            // The caller ignores the failure and tries to commit: that must not keep the change.
            await Should.ThrowAsync<InvalidOperationException>(() => tx.CommitAsync(Ct));
        }

        (await ItemExistsAsync(item.Id)).ShouldBeFalse("the business change must not survive a failed audit write");
        (await CountAuditAsync(correlation)).ShouldBe(0);
    }

    private async Task<bool> ItemExistsAsync(Guid id)
    {
        await using var check = db.CreateProbe();
        return await check.Items.AnyAsync(i => i.Id == id, Ct);
    }

    private async Task AppendCommittedAsync(string correlation)
    {
        await using var context = db.CreateProbe();
        await using var tx = await context.Database.BeginTransactionAsync(Ct);
        await Writer().AppendAsync(tx.GetDbTransaction(), Record(correlation), Ct);
        await tx.CommitAsync(Ct);
    }

    /// <summary>The grant layer: a permission error, and specifically not the trigger's message.</summary>
    private async Task ShouldBePermissionDeniedAsync(string connectionString, string constantSql)
    {
        var ex = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(connectionString, constantSql, Ct));
        ex.Message.ShouldMatch("(?i)permission", $"expected a permission error for: {constantSql}");
        ex.Message.ShouldNotContain(TriggerMessage, Case.Insensitive, $"the grant layer, not the trigger, must refuse: {constantSql}");
    }

    private string RawInsert(bool includeSeq)
    {
        string[] columns = ["Id", "RecordedAtUtc", "OccurredAtUtc", "ActorId", "Action", "TargetType", "Outcome", "CorrelationId", "PayloadCanonical", "RowHash", "SchemaVersion"];
        var now = IsSqlServer ? "SYSDATETIMEOFFSET()" : "now()";
        var hash = IsSqlServer ? "REPLICATE('0', 64)" : "repeat('0', 64)";
        var values = $"'{Guid.CreateVersion7()}', {now}, {now}, 'raw', 'parity.item.created', 'parity.item', 'Succeeded', '{NewCorrelation()}', '{{}}', {hash}, 1";
        if (includeSeq)
        {
            columns = [.. columns, "Seq"];
            values += ", 900000000";
        }

        return $"INSERT INTO {Table} ({string.Join(", ", columns.Select(Col))}) VALUES ({values})";
    }

    private Task<long> CountAllAsync() => ScalarLongAsync(db.AuditAppConnectionString, $"SELECT COUNT(*) FROM {Table}");

    private async Task<long> ScalarLongAsync(string connectionString, string constantSql) =>
        Convert.ToInt64(await db.ScalarAsync(connectionString, constantSql, Ct), CultureInfo.InvariantCulture);

    private async Task<long> CountAuditAsync(string correlation) => (await ReadRowsAsync(correlation)).Count;

    private async Task<StoredRow> ReadRowAsync(string correlation) => (await ReadRowsAsync(correlation)).ShouldHaveSingleItem();

    [SuppressMessage("Security", "CA2100", Justification = "Constant SQL built from compile-time identifiers; the correlation id is a bound parameter.")]
    private async Task<List<StoredRow>> ReadRowsAsync(string correlation)
    {
        string[] columns =
        [
            "Seq", "Id", "RecordedAtUtc", "OccurredAtUtc", "ActorId", "EffectiveActorId", "Action", "TargetType", "TargetId",
            "Outcome", "Reason", "CorrelationId", "CausationId", "PolicyVersionRef", "PolicyVersionHash", "PayloadCanonical",
            "SchemaVersion", "RowHash",
        ];
        await using var connection = db.OpenConnection(db.AuditAppConnectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(Col))} FROM {Table} WHERE {Col("CorrelationId")} = @c";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@c";
        parameter.Value = correlation;
        command.Parameters.Add(parameter);

        var rows = new List<StoredRow>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            DateTimeOffset Time(int i) => reader.GetFieldValue<DateTimeOffset>(i).ToUniversalTime();
            var row = new ValidatedAuditEvent(
                reader.GetGuid(1), Time(2), Time(3), reader.GetString(4), Text(5), reader.GetString(6), reader.GetString(7),
                Text(8), reader.GetString(9), Text(10), reader.GetString(11), Text(12), Text(13), Text(14), reader.GetString(15),
                reader.GetInt32(16))
            {
                RowHash = reader.GetString(17),
            };
            rows.Add(new StoredRow(reader.GetInt64(0), row));
        }

        return rows;
    }

    private sealed record StoredRow(long Seq, ValidatedAuditEvent Row);
}
