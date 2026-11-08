using Shouldly;

namespace IAM.Api.Tests;

/// <summary>S1-06: the API refuses to start without an explicit provider and a connection string per module schema.</summary>
public sealed class ApiPersistenceStartupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sqlserver")]
    [InlineData("Sqlite")]
    [InlineData("1")]
    public void ApiRefusesToStartWithoutAValidProvider(string? provider)
    {
        using var host = new ApiTestHost(databaseProvider: provider);
        var ex = Should.Throw<Exception>(() => host.CreateClient());
        Flatten(ex).ShouldContain(e => e is InvalidOperationException && e.Message.Contains("IAM_DB_PROVIDER", StringComparison.Ordinal));
    }

    [Fact]
    public void ApiRefusesToStartWithoutModuleConnectionString()
    {
        using var host = new ApiTestHost(identityConnectionString: null);
        var ex = Should.Throw<Exception>(() => host.CreateClient());
        Flatten(ex).ShouldContain(e => e is InvalidOperationException && e.Message.Contains("ConnectionStrings:identity", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("PostgreSql")]
    public async Task ApiStartsWithEitherProvider(string provider)
    {
        using var host = new ApiTestHost(databaseProvider: provider);
        using var client = host.CreateClient();
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
