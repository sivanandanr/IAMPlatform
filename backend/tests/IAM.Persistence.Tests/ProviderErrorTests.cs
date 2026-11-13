using IAM.Persistence.PostgreSql;
using Npgsql;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>
/// Which database errors mean "lost to a concurrent transaction" (a conflict to report) rather than a fault. Only the
/// PostgreSQL side can be unit tested: SqlException has no public constructor; SQL Server deadlocks are covered by the
/// parity race tests.
/// </summary>
public sealed class ProviderErrorTests
{
    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure, true)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, true)]
    [InlineData(PostgresErrorCodes.UniqueViolation, false)]
    [InlineData(PostgresErrorCodes.CheckViolation, false)]
    [InlineData(PostgresErrorCodes.RaiseException, false)]
    public void PostgreSqlConcurrencyFailuresAreRecognised(string sqlState, bool expected)
    {
        var provider = new PostgreSqlDatabaseProvider();
        var error = new PostgresException("test", "ERROR", "ERROR", sqlState);
        provider.IsConcurrencyFailure(error).ShouldBe(expected);
        provider.IsConcurrencyFailure(new InvalidOperationException("wrapped", error)).ShouldBe(expected);
        provider.IsConcurrencyFailure(new InvalidOperationException("other")).ShouldBeFalse();
    }
}
