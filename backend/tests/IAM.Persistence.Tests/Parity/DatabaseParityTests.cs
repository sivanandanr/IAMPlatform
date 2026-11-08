using System.Diagnostics.CodeAnalysis;
using IAM.BuildingBlocks.Persistence;
using IAM.Persistence.Tests.Probe;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// S1-06 behaviour that must be identical on SQL Server and PostgreSQL (ADR-0010, ADR-0017). The CI parity job runs
/// this class once per engine; neither leg may be skipped.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class DatabaseParityTests(ParityDatabase db)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Migrations -------------------------------------------------------------------------------------------

    [Fact]
    public async Task MigrationsApplyAndReRunCleanly()
    {
        db.FirstRunApplied.ShouldNotBeEmpty();
        db.SecondRunPending.ShouldBeEmpty();

        await using var identity = TestSupport.CreateIdentity(db.Provider, db.MigratorConnectionString);
        (await identity.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(identity.Database.GetMigrations());
    }

    [Fact]
    public async Task IdentitySchemaHoldsExactlyTheCatalogTables()
    {
        var tables = (await StringListAsync(
            db.AdminConnectionString,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'identity'"))
            .Order(StringComparer.Ordinal)
            .ToList();
        string[] expected = db.Provider == DatabaseProvider.SqlServer
            ? ["AttributeDefinition", "AttributeDefinitionVersion", "AttributeDefinitionVersionOperator", "__EFMigrationsHistory"]
            : ["__ef_migrations_history", "attribute_definition", "attribute_definition_version", "attribute_definition_version_operator"];
        tables.ShouldBe(expected.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task ProbeSchemaMatchesTheConventions()
    {
        var columns = await StringListAsync(
            db.AdminConnectionString,
            db.Provider == DatabaseProvider.SqlServer
                ? "SELECT c.name + ':' + t.name + ':' + CAST(c.max_length AS varchar(10)) + ':' + CAST(c.precision AS varchar(10)) + ':' + CAST(c.scale AS varchar(10)) + ':' + ISNULL(c.collation_name, '-') + ':' + CASE c.is_nullable WHEN 1 THEN 'null' ELSE 'not null' END " +
                  "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'parity_probe.ProbeItems') ORDER BY c.name"
                : "SELECT column_name || ':' || data_type || ':' || coalesce(character_maximum_length::text, '-') || ':' || coalesce(numeric_precision::text, datetime_precision::text, '-') || ':' || coalesce(numeric_scale::text, '-') || ':' || coalesce(collation_name, '-') || ':' || CASE is_nullable WHEN 'YES' THEN 'null' ELSE 'not null' END " +
                  "FROM information_schema.columns WHERE table_schema = 'parity_probe' AND table_name = 'probe_items' ORDER BY column_name");

        string[] expected = db.Provider == DatabaseProvider.SqlServer
            ?
            [
                "Amount:decimal:13:28:8:-:not null",
                "Code:nvarchar:64:0:0:Latin1_General_100_BIN2:not null",
                "CreatedAtUtc:datetimeoffset:10:33:6:-:not null",
                "CreatedBy:nvarchar:400:0:0:SQL_Latin1_General_CP1_CI_AS:not null",
                "DisplayName:nvarchar:400:0:0:SQL_Latin1_General_CP1_CI_AS:not null",
                "EffectiveAtUtc:datetimeoffset:10:33:6:-:not null",
                "ExternalRef:nvarchar:128:0:0:Latin1_General_100_BIN2:null",
                "Id:uniqueidentifier:16:0:0:-:not null",
                "ModifiedAtUtc:datetimeoffset:10:33:6:-:not null",
                "ModifiedBy:nvarchar:400:0:0:SQL_Latin1_General_CP1_CI_AS:not null",
                "Version:bigint:8:19:0:-:not null",
            ]
            :
            [
                "amount:numeric:-:28:8:-:not null",
                "code:character varying:32:-:-:C:not null",
                "created_at_utc:timestamp with time zone:-:6:-:-:not null",
                "created_by:character varying:200:-:-:-:not null",
                "display_name:character varying:200:-:-:-:not null",
                "effective_at_utc:timestamp with time zone:-:6:-:-:not null",
                "external_ref:character varying:64:-:-:C:null",
                "id:uuid:-:-:-:-:not null",
                "modified_at_utc:timestamp with time zone:-:6:-:-:not null",
                "modified_by:character varying:200:-:-:-:not null",
                "version:bigint:-:64:0:-:not null",
            ];
        columns.ShouldBe(expected);
    }

    // ---- Identifiers -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ApplicationGeneratedUuidV7RoundTrips()
    {
        var item = NewItem();
        item.Id.Version.ShouldBe(7);
        await SaveAsync(item);

        await using var read = db.CreateProbe();
        (await read.Items.SingleAsync(x => x.Id == item.Id, Ct)).Id.ShouldBe(item.Id);
    }

    [Fact]
    public async Task EmptyIdIsRejectedBeforeReachingTheDatabase()
    {
        await using var context = db.CreateProbe();
        context.Items.Add(NewItem(id: Guid.Empty));
        (await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(Ct))).Message.ShouldContain("empty Id");
    }

    // ---- Audit columns and concurrency -----------------------------------------------------------------------

    [Fact]
    public async Task AuditColumnsComeFromTheServerActorAndClock()
    {
        var clock = new FixedClock(T0.AddTicks(1_234_567));
        var item = NewItem();
        await using (var create = db.CreateProbe("alice", clock))
        {
            create.Items.Add(item);
            await create.SaveChangesAsync(Ct);
        }

        clock.Now = T0.AddHours(1);
        await using (var update = db.CreateProbe("bob", clock))
        {
            var loaded = await update.Items.SingleAsync(x => x.Id == item.Id, Ct);
            loaded.DisplayName = "renamed";
            var entry = update.Entry(loaded);
            entry.Property(AuditColumns.CreatedBy).CurrentValue = "mallory";
            entry.Property(AuditColumns.CreatedAtUtc).CurrentValue = T0.AddYears(-5);
            await update.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateProbe();
        var stored = read.Entry(await read.Items.SingleAsync(x => x.Id == item.Id, Ct));
        stored.Property(AuditColumns.CreatedBy).CurrentValue.ShouldBe("alice");
        stored.Property(AuditColumns.CreatedAtUtc).CurrentValue.ShouldBe(T0.AddTicks(1_234_560));
        stored.Property(AuditColumns.ModifiedBy).CurrentValue.ShouldBe("bob");
        stored.Property(AuditColumns.ModifiedAtUtc).CurrentValue.ShouldBe(T0.AddHours(1));
        stored.Property(AuditColumns.Version).CurrentValue.ShouldBe(2L);
    }

    [Fact]
    public async Task SavingWithoutAnActorFails()
    {
        await using var context = db.CreateProbe(actor: null);
        context.Items.Add(NewItem());
        (await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(Ct))).Message.ShouldContain("actor");
    }

    [Fact]
    public async Task StaleUpdateIsRejected()
    {
        var item = NewItem();
        await SaveAsync(item);

        await using var first = db.CreateProbe();
        await using var second = db.CreateProbe();
        var a = await first.Items.SingleAsync(x => x.Id == item.Id, Ct);
        var b = await second.Items.SingleAsync(x => x.Id == item.Id, Ct);

        a.DisplayName = "first writer";
        await first.SaveChangesAsync(Ct);

        b.DisplayName = "second writer";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    // ---- Time ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TimestampsAreStoredAsUtcMicroseconds()
    {
        var item = NewItem();
        item.EffectiveAtUtc = T0.AddTicks(9_999_999);
        await SaveAsync(item);

        await using var read = db.CreateProbe();
        var stored = await read.Items.SingleAsync(x => x.Id == item.Id, Ct);
        stored.EffectiveAtUtc.ShouldBe(T0.AddTicks(9_999_990));
        stored.EffectiveAtUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task NonUtcTimestampsAreRejected()
    {
        await using var context = db.CreateProbe();
        var item = NewItem();
        item.EffectiveAtUtc = new DateTimeOffset(2026, 11, 8, 11, 30, 0, TimeSpan.FromHours(2));
        context.Items.Add(item);
        var ex = await Should.ThrowAsync<Exception>(() => context.SaveChangesAsync(Ct));
        TestSupport.Flatten(ex).ShouldContain(e => e is ArgumentException && e.Message.Contains("UTC", StringComparison.Ordinal));
    }

    // ---- Codes -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CodesCompareExactlyWithLeadingZerosAndCase()
    {
        var prefix = UniquePrefix();
        string[] codes = [prefix + "01", prefix + "1", prefix + "ab", prefix + "AB"];
        await SaveAsync(codes.Select(c => NewItem(code: c)).ToArray());

        await using var read = db.CreateProbe();
        foreach (var code in codes)
        {
            var matches = await read.Items.Where(x => x.Code == code).Select(x => x.Code).ToListAsync(Ct);
            matches.ShouldBe([code]);
        }

        var ordered = await read.Items.Where(x => x.Code.StartsWith(prefix)).OrderBy(x => x.Code).Select(x => x.Code).ToListAsync(Ct);
        ordered.ShouldBe([prefix + "01", prefix + "1", prefix + "AB", prefix + "ab"], "binary ordering must match on both engines");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 01")]
    [InlineData("01 ")]
    public async Task CodesRejectEmptyAndPaddedValues(string code)
    {
        await using var context = db.CreateProbe();
        context.Items.Add(NewItem(code: code));
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task DuplicateCodeIsRejected()
    {
        var code = UniquePrefix() + "dup";
        await SaveAsync(NewItem(code: code));
        await using var context = db.CreateProbe();
        context.Items.Add(NewItem(code: code));
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task UniqueNullableColumnAllowsManyNullsButNoDuplicateValues()
    {
        await SaveAsync(NewItem(externalRef: null), NewItem(externalRef: null));

        var reference = UniquePrefix() + "ext";
        await SaveAsync(NewItem(externalRef: reference));
        await using var context = db.CreateProbe();
        context.Items.Add(NewItem(externalRef: reference));
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task DecimalsKeepEightDecimalPlaces()
    {
        var item = NewItem();
        item.Amount = 12345678901234567890.12345678m;
        await SaveAsync(item);

        await using var read = db.CreateProbe();
        (await read.Items.SingleAsync(x => x.Id == item.Id, Ct)).Amount.ShouldBe(12345678901234567890.12345678m);
    }

    [Fact]
    public async Task UnicodeTextRoundTrips()
    {
        const string text = "Zoë Ñúñez – 名前 – Ελληνικά – 🙂";
        var item = NewItem();
        item.DisplayName = text;
        await SaveAsync(item);

        await using var read = db.CreateProbe();
        (await read.Items.SingleAsync(x => x.Id == item.Id, Ct)).DisplayName.ShouldBe(text);
    }

    // ---- Isolation -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadersAreNotBlockedByUncommittedWriters()
    {
        var item = NewItem();
        item.DisplayName = "committed";
        await SaveAsync(item);

        await using var writer = db.CreateProbe();
        await using var transaction = await writer.Database.BeginTransactionAsync(Ct);
        var loaded = await writer.Items.SingleAsync(x => x.Id == item.Id, Ct);
        loaded.DisplayName = "uncommitted";
        await writer.SaveChangesAsync(Ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var reader = db.CreateProbe();
        var seen = await reader.Items.Where(x => x.Id == item.Id).Select(x => x.DisplayName).SingleAsync(timeout.Token);
        seen.ShouldBe("committed", "READ COMMITTED must read the last committed version without blocking (RCSI on SQL Server)");

        await transaction.RollbackAsync(Ct);
    }

    // ---- Least privilege -------------------------------------------------------------------------------------

    [Fact]
    public async Task ApplicationLoginCannotChangeTheSchema()
    {
        var ex = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
            db.ProbeAppConnectionString,
            db.Provider == DatabaseProvider.SqlServer
                ? "CREATE TABLE parity_probe.injected (id int);"
                : "CREATE TABLE parity_probe.injected (id int);",
            Ct));
        ex.Message.ShouldContain("permission", Case.Insensitive);
    }

    [Fact]
    public async Task ApplicationLoginCannotDropModuleTables()
    {
        var ex = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
            db.ProbeAppConnectionString,
            db.Provider == DatabaseProvider.SqlServer ? "DROP TABLE parity_probe.ProbeItems;" : "DROP TABLE parity_probe.probe_items;",
            Ct));
        ex.Message.ShouldMatch("(?i)(permission|owner)");
    }

    [Fact]
    public async Task ApplicationLoginCannotReadAnotherModuleSchema()
    {
        await SaveAsync(NewItem());
        var ex = await Should.ThrowAsync<Exception>(() => db.ScalarAsync(
            db.IdentityAppConnectionString,
            db.Provider == DatabaseProvider.SqlServer
                ? "SELECT COUNT(*) FROM parity_probe.ProbeItems;"
                : "SELECT COUNT(*) FROM parity_probe.probe_items;",
            Ct));
        ex.Message.ShouldContain("permission", Case.Insensitive);
    }

    [Fact]
    public async Task MigratorIsNotAnAdministrator()
    {
        var result = await db.ScalarAsync(
            db.MigratorConnectionString,
            db.Provider == DatabaseProvider.SqlServer
                ? "SELECT CAST(IS_SRVROLEMEMBER('sysadmin') + IS_ROLEMEMBER('db_owner') AS int);"
                : "SELECT (CASE WHEN rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls THEN 1 ELSE 0 END) FROM pg_roles WHERE rolname = current_user;",
            Ct);
        Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static ProbeItem NewItem(Guid? id = null, string? code = null, string? externalRef = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        Code = code ?? UniquePrefix(),
        ExternalRef = externalRef,
        DisplayName = "probe",
        EffectiveAtUtc = T0,
        Amount = 1m,
    };

    private static string UniquePrefix() => "p" + Guid.NewGuid().ToString("N")[..12];

    private async Task SaveAsync(params ProbeItem[] items)
    {
        await using var context = db.CreateProbe();
        context.Items.AddRange(items);
        await context.SaveChangesAsync(Ct);
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test helper: SQL text is a constant in test code; no user input.")]
    private async Task<List<string>> StringListAsync(string connectionString, string constantSql)
    {
        await using var connection = db.OpenConnection(connectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = constantSql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
