using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.Persistence.Tests.Parity;
using IAM.Tools.AccessBootstrap;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>P0-013 slice 5: the one-time bootstrap tool's command line and configuration (no database).</summary>
public sealed class AccessBootstrapToolTests
{
    private static readonly string[] Valid =
        ["--administrator", "admin-sub", "--manager", "manager-sub", "--operator", "ops.jane", "--approval", "CHG-1234", "--confirm"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ValidArgumentsParse()
    {
        var parsed = BootstrapArguments.Parse(Valid, out var error).ShouldNotBeNull();
        error.ShouldBeNull();
        parsed.ShouldBe(new BootstrapArguments("admin-sub", "manager-sub", "ops.jane", "CHG-1234"));
    }

    [Theory]
    [InlineData("--administrator", "a", "--manager", "m", "--operator", "op", "--approval", "CHG-1")]
    [InlineData("--administrator", "a", "--manager", "m", "--operator", "op", "--confirm")]
    [InlineData("--administrator", "a", "--administrator", "b", "--manager", "m", "--operator", "op", "--approval", "CHG-1", "--confirm")]
    [InlineData("--administrator", "a", "--manager", "m", "--operator", "op", "--approval", "CHG-1", "--confirm", "--password", "x")]
    [InlineData("--administrator", "a", "--manager", "m", "--operator", "Ops Jane", "--approval", "CHG-1", "--confirm")]
    [InlineData("--administrator", "a", "--manager", "m", "--operator", "", "--approval", "CHG-1", "--confirm")]
    [InlineData("--administrator", "a", "--manager", "m", "--operator", "op", "--approval", "x", "--confirm")]
    [InlineData("--administrator", "a", "--manager", "m", "--operator", "op", "--approval", "CHG 1; DROP", "--confirm")]
    [InlineData("--administrator", "--manager", "m", "--operator", "op", "--approval", "CHG-1", "--confirm")]
    [InlineData("--administrator", "a", "--manager")]
    public void AnythingElseIsRefusedBeforeConnecting(params string[] args)
    {
        BootstrapArguments.Parse(args, out var error).ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MissingConfigurationFailsWithoutConnecting()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        (await BootstrapTool.RunAsync(Valid, Config(), output, Ct)).ShouldBe(BootstrapTool.Failed);
        (await BootstrapTool.RunAsync(Valid, Config(("IAM_DB_PROVIDER", "PostgreSql")), output, Ct)).ShouldBe(BootstrapTool.Failed);
        output.ToString().ShouldContain("ConnectionStrings__bootstrap");
    }

    [Theory]
    [InlineData("Host=db.invalid;Database=iam;Username=iam_app_administration;Password=x")]
    [InlineData("Host=db.invalid;Database=iam;Username=postgres;Password=x")]
    [InlineData("Host=db.invalid;Database=iam")]
    public async Task AnyLoginButTheDedicatedBootstrapLoginIsRefused(string connectionString)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await BootstrapTool.RunAsync(Valid, Config(("IAM_DB_PROVIDER", "PostgreSql"), ("ConnectionStrings:bootstrap", connectionString)), output, Ct);
        exit.ShouldBe(BootstrapTool.Failed);
        output.ToString().ShouldContain("Refusing to run with this login");
    }

    [Fact]
    public async Task AnUnreachableDatabaseIsExitCode1WithoutDetails()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await BootstrapTool.RunAsync(
            Valid,
            Config(("IAM_DB_PROVIDER", "PostgreSql"), ("ConnectionStrings:bootstrap", "Host=unreachable.invalid;Database=iam;Username=iam_bootstrap_x;Password=probe-secret-value;Timeout=3")),
            output,
            Ct);
        exit.ShouldBe(BootstrapTool.Failed);
        var text = output.ToString();
        text.ShouldContain("Failed:");
        text.ShouldNotContain("unreachable.invalid");
        text.ShouldNotContain("probe-secret-value");
        text.ShouldNotContain("iam_bootstrap_x");
    }

    internal static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
}

/// <summary>
/// The tool against a real database, with the dedicated login from the reviewed grant script (DL-063a): the store is
/// already set up, so it is refused, changes nothing and is audited under the operator. The login can't change anything
/// beyond the bootstrap's inserts, the API's own login is refused, and a revoked login fails cleanly.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class AccessBootstrapToolParityTests(ParityDatabase db)
{
    private static readonly string[] Args =
        ["--administrator", "new-admin", "--manager", "new-manager", "--operator", "ops.parity", "--approval", "CHG-PARITY", "--confirm"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task TheDedicatedLoginRunsTheToolAndARevokedOneFailsCleanly()
    {
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        const string login = "iam_bootstrap_parity";
        var password = ParityDatabase.NewPassword();
        await db.GrantAccessBootstrapLoginAsync(login, password, Ct);
        var bootstrap = db.ConnectionStringFor(login, password);

        var versionsSql = IsSqlServer ? "SELECT COUNT(*) FROM [configuration].[AccessModelVersion]" : "SELECT COUNT(*) FROM configuration.access_model_version";
        var refusedSql = IsSqlServer
            ? "SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [Action] = 'administration.access_change.refused' AND [ActorId] = 'system:bootstrap:ops.parity'"
            : "SELECT COUNT(*) FROM audit.audit_event WHERE action = 'administration.access_change.refused' AND actor_id = 'system:bootstrap:ops.parity'";
        var versionsBefore = await CountAsync(db.AdministrationAppConnectionString, versionsSql);
        var refusedBefore = await CountAsync(db.AuditAppConnectionString, refusedSql);

        using (var output = new StringWriter(CultureInfo.InvariantCulture))
        {
            var exit = await BootstrapTool.RunAsync(Args, Config(bootstrap), output, Ct);
            exit.ShouldBe(BootstrapTool.Refused, output.ToString());
            output.ToString().ShouldContain("AlreadyBootstrapped");
            output.ToString().ShouldNotContain("new-admin");
        }

        (await CountAsync(db.AdministrationAppConnectionString, versionsSql)).ShouldBe(versionsBefore);
        (await CountAsync(db.AuditAppConnectionString, refusedSql)).ShouldBe(refusedBefore + 1);

        // Least privilege: the login can't update or delete anything, and can't read the audit log.
        var role = IsSqlServer ? "[configuration].[AccessRole]" : "configuration.access_role";
        await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(bootstrap, $"UPDATE {role} SET {(IsSqlServer ? "[Name]" : "name")} = 'x'", Ct));
        await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(bootstrap, $"DELETE FROM {role}", Ct));
        await Should.ThrowAsync<Exception>(() => db.ScalarAsync(bootstrap, IsSqlServer ? "SELECT COUNT(*) FROM [audit].[AuditEvent]" : "SELECT COUNT(*) FROM audit.audit_event", Ct));

        // The API's own login is refused by the tool itself.
        using (var output = new StringWriter(CultureInfo.InvariantCulture))
        {
            (await BootstrapTool.RunAsync(Args, Config(db.AdministrationAppConnectionString), output, Ct)).ShouldBe(BootstrapTool.Failed);
            output.ToString().ShouldContain("Refusing to run with this login");
        }

        await db.RevokeAccessBootstrapLoginAsync(login, Ct);
        using (var output = new StringWriter(CultureInfo.InvariantCulture))
        {
            (await BootstrapTool.RunAsync(Args, Config(bootstrap), output, Ct)).ShouldBe(BootstrapTool.Failed);
            output.ToString().ShouldContain("Failed:");
            output.ToString().ShouldNotContain(password);
        }

        (await CountAsync(db.AuditAppConnectionString, refusedSql)).ShouldBe(refusedBefore + 1);
    }

    private IConfiguration Config(string connectionString) =>
        AccessBootstrapToolTests.Config(("IAM_DB_PROVIDER", db.Provider.ToString()), ("ConnectionStrings:bootstrap", connectionString));

    private async Task<long> CountAsync(string connectionString, string sql) =>
        Convert.ToInt64(await db.ScalarAsync(connectionString, sql, Ct), CultureInfo.InvariantCulture);
}
