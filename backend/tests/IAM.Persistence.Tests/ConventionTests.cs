using System.Reflection;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Audit.Infrastructure.Persistence;
using IAM.Persistence.Tests.Probe;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>Model-level checks of the S1-06 conventions. No database is opened.</summary>
public sealed class ConventionTests
{
    [Theory]
    [InlineData("SqlServer", DatabaseProvider.SqlServer)]
    [InlineData("PostgreSql", DatabaseProvider.PostgreSql)]
    public void ProviderSettingAcceptsExactNames(string value, DatabaseProvider expected) =>
        DatabaseProviderSetting.Parse(value).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("sqlserver")]
    [InlineData("Postgres")]
    [InlineData("Sqlite")]
    [InlineData("InMemory")]
    [InlineData("1")]
    public void ProviderSettingFailsClosed(string? value) =>
        Should.Throw<InvalidOperationException>(() => DatabaseProviderSetting.Parse(value)).Message.ShouldContain("IAM_DB_PROVIDER");

    [Theory]
    [InlineData("CreatedAtUtc", "created_at_utc")]
    [InlineData("PK_ProbeItems", "pk_probe_items")]
    [InlineData("IX_ProbeItems_ExternalRef", "ix_probe_items_external_ref")]
    [InlineData("HTTPStatus", "http_status")]
    [InlineData("Version", "version")]
    [InlineData("already_snake", "already_snake")]
    public void SnakeCase(string input, string expected) => IamModelConventions.ToSnakeCase(input).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("Identity")]
    [InlineData("1identity")]
    [InlineData("identity;drop")]
    [InlineData("iden tity")]
    [InlineData("identity\n")]
    public void SchemaNamesAreAllowlisted(string? schema) =>
        Should.Throw<ArgumentException>(() => IamDbContext.ValidateSchemaName(schema!));

    [Fact]
    public void ConverterRejectsNonUtcAndTruncatesToMicroseconds()
    {
        Should.Throw<ArgumentException>(() => UtcMicrosecondConverter.ToStore(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(2))));
        var value = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
        UtcMicrosecondConverter.ToStore(value).ShouldBe(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560));
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void EveryTableGetsServerSetAuditAndVersionColumns(DatabaseProvider provider)
    {
        using var context = TestSupport.CreateProbe(provider, TestSupport.OfflineConnectionString(provider));
        var item = context.Model.FindEntityType(typeof(ProbeItem))!;

        foreach (var name in new[] { AuditColumns.CreatedAtUtc, AuditColumns.CreatedBy, AuditColumns.ModifiedAtUtc, AuditColumns.ModifiedBy, AuditColumns.Version })
        {
            var property = item.FindProperty(name).ShouldNotBeNull();
            property.IsShadowProperty().ShouldBeTrue($"{name} must be a shadow property so no DTO or entity setter can bind it");
            property.IsNullable.ShouldBeFalse();
        }

        item.FindProperty(AuditColumns.Version)!.IsConcurrencyToken.ShouldBeTrue();
        item.FindProperty(AuditColumns.Version)!.ClrType.ShouldBe(typeof(long));
        item.FindPrimaryKey()!.Properties.Single().ValueGenerated.ShouldBe(ValueGenerated.Never);
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer, "datetimeoffset(6)", "decimal(28,8)", "Latin1_General_100_BIN2", "ProbeItems", "CreatedAtUtc")]
    [InlineData(DatabaseProvider.PostgreSql, "timestamp(6) with time zone", "numeric(28,8)", "C", "probe_items", "created_at_utc")]
    public void ColumnTypesCollationsAndNamesFollowTheProvider(
        DatabaseProvider provider, string timestampType, string decimalType, string codeCollation, string table, string createdColumn)
    {
        using var context = TestSupport.CreateProbe(provider, TestSupport.OfflineConnectionString(provider));
        var item = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ProbeItem))!;

        item.GetTableName().ShouldBe(table);
        item.GetSchema().ShouldBe(ProbeDbContext.SchemaName);
        item.FindProperty(AuditColumns.CreatedAtUtc)!.GetColumnName().ShouldBe(createdColumn);
        item.FindProperty(nameof(ProbeItem.EffectiveAtUtc))!.GetColumnType().ShouldBe(timestampType);
        item.FindProperty(nameof(ProbeItem.Amount))!.GetColumnType().ShouldBe(decimalType);
        item.FindProperty(nameof(ProbeItem.Code))!.GetCollation().ShouldBe(codeCollation);
        item.FindProperty(nameof(ProbeItem.Code))!.GetMaxLength().ShouldBe(32);
        item.FindProperty(nameof(ProbeItem.DisplayName))!.GetCollation().ShouldBeNull("display text keeps the default collation");
        item.GetCheckConstraints().Count().ShouldBe(2, "one padding/empty check per code column");
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void IdentityMigrationsMatchTheModel(DatabaseProvider provider)
    {
        using var context = TestSupport.CreateIdentity(provider, TestSupport.OfflineConnectionString(provider));
        context.Database.GetMigrations().ShouldNotBeEmpty();
        context.Database.HasPendingModelChanges().ShouldBeFalse(
            $"the {provider} migrations are out of date: add a reviewed migration for each provider (ADR-0003)");
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void AuditMigrationsMatchTheModel(DatabaseProvider provider)
    {
        using var context = TestSupport.CreateAudit(provider, TestSupport.OfflineConnectionString(provider));
        context.Database.GetMigrations().ShouldNotBeEmpty();
        context.Database.HasPendingModelChanges().ShouldBeFalse(
            $"the {provider} audit migrations are out of date: add a reviewed migration for each provider (ADR-0003)");
    }

    /// <summary>S1-07: the audit table is append-only, so it has no Modified*/Version columns and a time-leading key.</summary>
    [Theory]
    [InlineData(DatabaseProvider.SqlServer, "AuditEvent", "datetimeoffset(6)")]
    [InlineData(DatabaseProvider.PostgreSql, "audit_event", "timestamp(6) with time zone")]
    public void AuditEventIsAppendOnly(DatabaseProvider provider, string table, string timestampType)
    {
        using var context = TestSupport.CreateAudit(provider, TestSupport.OfflineConnectionString(provider));
        var row = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditEventRow))!;

        row.IsAppendOnlyTable().ShouldBeTrue();
        row.GetTableName().ShouldBe(table);
        row.GetSchema().ShouldBe("audit");
        foreach (var name in new[] { AuditColumns.CreatedAtUtc, AuditColumns.CreatedBy, AuditColumns.ModifiedAtUtc, AuditColumns.ModifiedBy, AuditColumns.Version })
        {
            row.FindProperty(name).ShouldBeNull($"append-only rows are never modified, so they have no {name} column");
        }

        row.FindPrimaryKey()!.Properties.Select(p => p.Name).ShouldBe([nameof(AuditEventRow.RecordedAtUtc), nameof(AuditEventRow.Id)]);
        row.FindProperty(nameof(AuditEventRow.Id))!.ValueGenerated.ShouldBe(ValueGenerated.Never);
        row.FindProperty(nameof(AuditEventRow.Seq))!.ValueGenerated.ShouldBe(ValueGenerated.OnAdd);
        row.FindProperty(nameof(AuditEventRow.RecordedAtUtc))!.GetColumnType().ShouldBe(timestampType);
        row.FindProperty(nameof(AuditEventRow.Action))!.GetCollation().ShouldBe(CodeCollations.For(provider));
        row.FindProperty(nameof(AuditEventRow.RowHash))!.GetMaxLength().ShouldBe(64);
    }

    /// <summary>The writer's hand-built INSERT must name exactly the model's columns except the database-assigned Seq.</summary>
    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void TheWriterInsertMatchesTheModel(DatabaseProvider provider)
    {
        using var context = TestSupport.CreateAudit(provider, TestSupport.OfflineConnectionString(provider));
        var row = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditEventRow))!;
        var modelColumns = row.GetProperties()
            .Where(p => p.Name != nameof(AuditEventRow.Seq))
            .Select(p => p.GetColumnName())
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        var writerColumns = AuditWriter.InsertColumns
            .Select(c => provider == DatabaseProvider.PostgreSql ? IamModelConventions.ToSnakeCase(c) : c)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        writerColumns.ShouldBe(modelColumns);
        var sql = AuditWriter.InsertSql(provider);
        foreach (var column in modelColumns)
        {
            sql.ShouldContain(provider == DatabaseProvider.SqlServer ? $"[{column}]" : $"\"{column}\"");
        }

        sql.ShouldNotContain(provider == DatabaseProvider.SqlServer ? "[Seq]" : "\"seq\"");
        Should.Throw<ArgumentOutOfRangeException>(() => AuditWriter.InsertSql((DatabaseProvider)99));
    }

    [Fact]
    public void InvalidActionRegistrationsFailWhenTheCatalogIsBuilt()
    {
        Should.Throw<InvalidOperationException>(() => new AuditActionCatalog([new AuditActionRegistration("Not Valid")]));
        new AuditActionCatalog([new AuditActionRegistration("identity.session.started")]).Actions.ShouldContain("identity.session.started");
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public void TheInterceptorRefusesToChangeAppendOnlyRows(EntityState state)
    {
        using var context = TestSupport.CreateAudit(
            DatabaseProvider.PostgreSql,
            TestSupport.OfflineConnectionString(DatabaseProvider.PostgreSql),
            new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor("tester")));
        context.Entry(new AuditEventRow { Id = Guid.CreateVersion7(), RecordedAtUtc = DateTimeOffset.UtcNow }).State = state;

        Should.Throw<InvalidOperationException>(() => context.SaveChanges()).Message.ShouldContain("append-only");
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void ModelCacheIsKeyedByProvider(DatabaseProvider provider)
    {
        var other = provider == DatabaseProvider.SqlServer ? DatabaseProvider.PostgreSql : DatabaseProvider.SqlServer;
        using var first = TestSupport.CreateProbe(provider, TestSupport.OfflineConnectionString(provider));
        using var second = TestSupport.CreateProbe(other, TestSupport.OfflineConnectionString(other));
        first.Model.FindEntityType(typeof(ProbeItem))!.GetTableName()
            .ShouldNotBe(second.Model.FindEntityType(typeof(ProbeItem))!.GetTableName());
    }

    /// <summary>S1-02 rule: a module's DbContext lives in that module's schema and maps nothing outside it.</summary>
    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void ModuleDbContextsMapOnlyTheirOwnSchema(DatabaseProvider provider)
    {
        var contexts = ModuleDbContextTypes();
        contexts.ShouldNotBeEmpty();
        foreach (var type in contexts)
        {
            var module = type.Assembly.GetName().Name!.Split('.')[2];
            using var context = CreateModuleContext(type, provider);
            context.Schema.ShouldBe(module.ToLowerInvariant(), $"{type.Name} must use schema '{module.ToLowerInvariant()}'");
            foreach (var entity in context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null))
            {
                entity.GetSchema().ShouldBe(context.Schema, $"{type.Name} maps {entity.DisplayName()} outside its schema");
            }
        }
    }

    private static List<Type> ModuleDbContextTypes()
    {
        var directory = AppContext.BaseDirectory;
        return Directory.EnumerateFiles(directory, "IAM.Modules.*.Infrastructure.dll")
            .Select(path => Assembly.LoadFrom(path))
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => !t.IsAbstract && typeof(IamDbContext).IsAssignableFrom(t))
            .ToList();
    }

    private static IamDbContext CreateModuleContext(Type contextType, DatabaseProvider provider)
    {
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
        TestSupport.ProviderFor(provider).Configure(builder, TestSupport.OfflineConnectionString(provider), "placeholder");
        return (IamDbContext)Activator.CreateInstance(contextType, builder.Options)!;
    }
}
