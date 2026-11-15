using IAM.BuildingBlocks.Messaging.RabbitMq;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>Broker and relay settings are checked before a worker runs (DL-077). No broker needed.</summary>
public sealed class RabbitMqSettingsTests
{
    [Fact]
    public void TheBrokerNeedsItsOwnUserAndTlsAwayFromThisMachine()
    {
        var local = new RabbitMqSettings("localhost", null, "iam", "iam-workflow", "pw", useTls: false);
        local.Port.ShouldBe(5672);
        new RabbitMqSettings("::1", null, "iam", "iam-workflow", "pw", useTls: false).Host.ShouldBe("::1");
        var remote = new RabbitMqSettings("broker.internal", null, "iam", "iam-workflow", "pw", useTls: null);
        remote.UseTls.ShouldBeTrue();
        remote.Port.ShouldBe(5671);

        Should.Throw<InvalidOperationException>(() => new RabbitMqSettings("broker.internal", null, "iam", "iam-workflow", "pw", useTls: false));
        Should.Throw<InvalidOperationException>(() => new RabbitMqSettings("localhost", null, "iam", "guest", "guest", useTls: false));
        Should.Throw<InvalidOperationException>(() => new RabbitMqSettings("localhost", null, "iam", "GUEST", "pw", useTls: false));
        Should.Throw<InvalidOperationException>(() => new RabbitMqSettings(null, null, "iam", "u", "pw", useTls: false));
        Should.Throw<InvalidOperationException>(() => new RabbitMqSettings("localhost", null, " ", "u", "pw", useTls: false));
        Should.Throw<InvalidOperationException>(() => new RabbitMqSettings("localhost", null, "iam", "u", "", useTls: false));
        Should.Throw<ArgumentOutOfRangeException>(() => new RabbitMqSettings("localhost", 70000, "iam", "u", "pw", useTls: false));
        Should.Throw<ArgumentOutOfRangeException>(() => new RabbitMqSettings("localhost", null, "iam", "u", "pw", useTls: false, connectTimeoutSeconds: 0));
    }

    [Fact]
    public void BrokerSettingsNeverPrintThePassword()
    {
        var settings = new RabbitMqSettings("localhost", 5672, "iam", "iam-workflow", "s3cret-value", useTls: false);
        settings.ToString().ShouldNotContain("s3cret-value");
        settings.ToString().ShouldContain("iam-workflow@localhost:5672/iam");
    }

    [Fact]
    public void RelaySettingsAreBounded()
    {
        var defaults = new OutboxRelaySettings();
        defaults.Interval.ShouldBe(TimeSpan.FromSeconds(5));
        defaults.BatchSize.ShouldBe(100);
        defaults.MaxAttempts.ShouldBe(10);
        Should.Throw<ArgumentOutOfRangeException>(() => new OutboxRelaySettings(intervalSeconds: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new OutboxRelaySettings(intervalSeconds: 301));
        Should.Throw<ArgumentOutOfRangeException>(() => new OutboxRelaySettings(batchSize: 501));
        Should.Throw<ArgumentOutOfRangeException>(() => new OutboxRelaySettings(maxAttempts: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new OutboxRelaySettings(maxAttempts: 51));
        Should.Throw<ArgumentOutOfRangeException>(() => new OutboxRelaySettings(publishTimeoutSeconds: 61));
        Should.Throw<ArgumentOutOfRangeException>(() => new OutboxRelaySettings(publishTimeoutSeconds: 10, leaseSeconds: 10));
    }

    [Fact]
    public void BackoffDoublesUpToFifteenMinutes()
    {
        OutboxRelaySettings.Backoff(1).ShouldBe(TimeSpan.FromSeconds(2));
        OutboxRelaySettings.Backoff(3).ShouldBe(TimeSpan.FromSeconds(8));
        OutboxRelaySettings.Backoff(50).ShouldBe(TimeSpan.FromMinutes(15));
    }
}
