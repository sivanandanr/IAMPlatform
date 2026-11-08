using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Application;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure.Persistence;

namespace IAM.Modules.Audit.Infrastructure;

/// <summary>The registered audit action codes, checked once when the catalog is created (a singleton).</summary>
public sealed class AuditActionCatalog
{
    public AuditActionCatalog(IEnumerable<AuditActionRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var actions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            if (registration is null || !AuditEventValidator.IsValidActionCode(registration.Action))
            {
                throw new InvalidOperationException("An audit action registration is not a valid action code (ADR-0018).");
            }

            actions.Add(registration.Action);
        }

        Actions = actions;
    }

    public IReadOnlySet<string> Actions { get; }
}

/// <summary>
/// Appends audit rows on the caller's open transaction (ADR-0018, DL-022), so the row commits or rolls back with the
/// business change. If anything fails, the caller's transaction is rolled back before the exception is rethrown, so a
/// caller that catches the exception cannot commit the change without its audit row. The writer runs one constant,
/// parameterised INSERT and never reads the row back, so the caller's login needs only INSERT on
/// <c>audit.AuditEvent</c> (through <see cref="AuditDbContext.WriterRole"/>). The actor comes from the server-side
/// <see cref="IAuditActorAccessor"/>, never from the record.
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    /// <summary>Columns the writer supplies, as model property names. <c>Seq</c> is assigned by the database.</summary>
    public static readonly IReadOnlyList<string> InsertColumns =
    [
        nameof(AuditEventRow.Id), nameof(AuditEventRow.RecordedAtUtc), nameof(AuditEventRow.OccurredAtUtc),
        nameof(AuditEventRow.ActorId), nameof(AuditEventRow.EffectiveActorId), nameof(AuditEventRow.Action),
        nameof(AuditEventRow.TargetType), nameof(AuditEventRow.TargetId), nameof(AuditEventRow.Outcome),
        nameof(AuditEventRow.Reason), nameof(AuditEventRow.CorrelationId), nameof(AuditEventRow.CausationId),
        nameof(AuditEventRow.PolicyVersionRef), nameof(AuditEventRow.PolicyVersionHash),
        nameof(AuditEventRow.PayloadCanonical), nameof(AuditEventRow.RowHash), nameof(AuditEventRow.SchemaVersion),
    ];

    private static readonly string SqlServerInsert = BuildInsert(DatabaseProvider.SqlServer);
    private static readonly string PostgreSqlInsert = BuildInsert(DatabaseProvider.PostgreSql);

    private readonly string _insertSql;
    private readonly TimeProvider _clock;
    private readonly IAuditActorAccessor _actor;
    private readonly AuditActionCatalog _catalog;

    public AuditWriter(IIamDatabaseProvider databaseProvider, TimeProvider clock, IAuditActorAccessor actor, AuditActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(databaseProvider);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(catalog);
        _insertSql = InsertSql(databaseProvider.Provider);
        _clock = clock;
        _actor = actor;
        _catalog = catalog;
    }

    /// <summary>The INSERT statement for <paramref name="provider"/>; throws for an unsupported provider.</summary>
    public static string InsertSql(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => SqlServerInsert,
        DatabaseProvider.PostgreSql => PostgreSqlInsert,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), "Unsupported database provider."),
    };

    [SuppressMessage("Security", "CA2100", Justification = "Constant INSERT text built from compile-time column names; every value is a bound parameter.")]
    [SuppressMessage("Design", "CA1031", Justification = "A failed rollback must not hide the original failure, which is rethrown.")]
    public async Task<AuditWriteReceipt> AppendAsync(DbTransaction transaction, AuditEventRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(record);
        var connection = OpenConnectionOf(transaction)
            ?? throw new InvalidOperationException(
                "Audit events must be written inside an open transaction; this transaction has already completed (ADR-0018).");

        try
        {
            var row = AuditEventValidator.Validate(
                record,
                _actor.CurrentActor,
                _catalog.Actions,
                Guid.CreateVersion7(),
                UtcMicrosecondConverter.TruncateToMicroseconds(_clock.GetUtcNow()));

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = _insertSql;
            command.CommandTimeout = 30;
            object?[] values =
            [
                row.Id, row.RecordedAtUtc, row.OccurredAtUtc, row.ActorId, row.EffectiveActorId, row.Action, row.TargetType,
                row.TargetId, row.Outcome, row.Reason, row.CorrelationId, row.CausationId, row.PolicyVersionRef,
                row.PolicyVersionHash, row.PayloadCanonical, row.RowHash, row.SchemaVersion,
            ];
            for (var i = 0; i < values.Length; i++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@p{i}";
                parameter.DbType = values[i] switch
                {
                    Guid => DbType.Guid,
                    DateTimeOffset => DbType.DateTimeOffset,
                    int => DbType.Int32,
                    _ => DbType.String,
                };
                parameter.Value = values[i] ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (inserted != 1)
            {
                throw new InvalidOperationException("The audit event was not stored.");
            }

            return new AuditWriteReceipt(row.Id, row.RecordedAtUtc, row.RowHash);
        }
        catch
        {
            // Fail closed: the business change must not be committable without its audit row (IAM-FR-027).
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The original failure is what the caller needs; the transaction is unusable either way.
            }

            throw;
        }
    }

    private static DbConnection? OpenConnectionOf(DbTransaction transaction)
    {
        try
        {
            var connection = transaction.Connection;
            return connection is { State: ConnectionState.Open } ? connection : null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static string BuildInsert(DatabaseProvider provider)
    {
        var table = provider == DatabaseProvider.SqlServer
            ? $"[{AuditDbContext.SchemaName}].[AuditEvent]"
            : $"\"{AuditDbContext.SchemaName}\".\"audit_event\"";
        var columns = provider == DatabaseProvider.SqlServer
            ? InsertColumns.Select(c => $"[{c}]")
            : InsertColumns.Select(c => $"\"{IamModelConventions.ToSnakeCase(c)}\"");
        var parameters = InsertColumns.Select((_, i) => $"@p{i}");
        return $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", parameters)})";
    }
}
