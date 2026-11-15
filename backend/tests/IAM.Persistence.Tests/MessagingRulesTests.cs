using System.Runtime.CompilerServices;
using System.Text.Json;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.Modules.Notification.Infrastructure.Approvals;
using IAM.Modules.Workflow.Contracts;
using IAM.Persistence.Tests.Parity;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>
/// Rules the reviewed broker files and the consumers must keep (DL-079). No broker or database: these read the files in
/// <c>infra/rabbitmq</c> and the consumer types in the build.
/// </summary>
public sealed class MessagingRulesTests
{
    private static JsonElement Load(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RabbitMqBroker.Repository(file)));
        return document.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Queues() =>
        Load("definitions.json").GetProperty("queues").EnumerateArray().ToDictionary(q => q.GetProperty("name").GetString()!, q => q.GetProperty("arguments"));

    [Fact]
    public void EveryConsumerQueueDeadLettersAtLeastOnceWithTheBrokersBackStop()
    {
        var consumers = Queues().Where(q => q.Key.StartsWith("iam.", StringComparison.Ordinal) && q.Key != RabbitMqTopology.UnroutedQueue
            && !q.Key.EndsWith(".dlq", StringComparison.Ordinal) && !q.Key.EndsWith(".retry", StringComparison.Ordinal)).ToList();
        consumers.ShouldNotBeEmpty();
        foreach (var (name, arguments) in consumers)
        {
            arguments.GetProperty("x-queue-type").GetString().ShouldBe("quorum", name);
            arguments.GetProperty("x-delivery-limit").GetInt32().ShouldBe(ConsumerSettings.BrokerDeliveryLimit, name);
            arguments.GetProperty("x-dead-letter-strategy").GetString().ShouldBe("at-least-once", name);
            arguments.GetProperty("x-overflow").GetString().ShouldBe("reject-publish", name);
            arguments.GetProperty("x-dead-letter-exchange").GetString().ShouldBe(name + ".dlx", "each consumer has its own dead-letter exchange");
            arguments.GetProperty("x-dead-letter-routing-key").GetString().ShouldBe(name);
        }

        foreach (var (name, arguments) in Queues().Where(q => q.Key.EndsWith(".retry", StringComparison.Ordinal)))
        {
            arguments.GetProperty("x-dead-letter-strategy").GetString().ShouldBe("at-least-once", name);
            arguments.GetProperty("x-overflow").GetString().ShouldBe("reject-publish", name);
            arguments.GetProperty("x-dead-letter-exchange").GetString().ShouldBe(name[..^".retry".Length] + ".return", name);
        }
    }

    [Fact]
    public void TheUnroutedQueueIsCappedAndShortLived()
    {
        var unrouted = Queues()[RabbitMqTopology.UnroutedQueue];
        unrouted.GetProperty("x-max-length").GetInt32().ShouldBeInRange(1, 1_000_000);
        unrouted.GetProperty("x-max-length-bytes").GetInt64().ShouldBeInRange(1, 1L << 30);
        unrouted.GetProperty("x-overflow").GetString().ShouldBe("drop-head");
        unrouted.GetProperty("x-message-ttl").GetInt64().ShouldBeLessThanOrEqualTo((long)TimeSpan.FromDays(7).TotalMilliseconds);
    }

    [Fact]
    public void NoModuleMayChangeTheTopology()
    {
        var permissions = Load("permissions.json");
        foreach (var user in permissions.GetProperty("users").EnumerateArray())
        {
            var name = user.GetProperty("user").GetString()!;
            name.ShouldNotBe("guest");
            user.GetProperty("configure").GetString().ShouldBe("^$", name);
            foreach (var pattern in new[] { "write", "read" })
            {
                var value = user.GetProperty(pattern).GetString()!;
                value.ShouldStartWith("^", Case.Sensitive, name);
                value.ShouldEndWith("$", Case.Sensitive, name);
                value.ShouldNotContain(".*", Case.Sensitive, $"{name} {pattern}: no open-ended reach");
            }
        }
    }

    [Fact]
    public void NoConsumerTakesAuthorisationUntilTheAuditCheckExists()
    {
        // R-037 release gate (DL-075, DL-079): acting on authorisation needs the audit-chain verifier first. When it
        // exists, a consumer that uses it is allowed here and nowhere else.
        string[] gated = [ApprovalAuthorized.MessageType, ApprovalClosed.MessageType];
        var handlers = typeof(ApprovalTaskNotifications).Assembly.GetTypes()
            .Concat(typeof(MessagingRulesTests).Assembly.GetTypes())
            .Where(t => typeof(IMessageHandler).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToList();
        handlers.ShouldContain(typeof(ApprovalTaskNotifications));
        foreach (var type in handlers)
        {
            var handler = (IMessageHandler)RuntimeHelpers.GetUninitializedObject(type);
            handler.MessageTypes.ShouldNotBeNull($"{type.Name} must declare its types without state");
            handler.MessageTypes.Intersect(gated).ShouldBeEmpty($"{type.Name} takes an event that authorises provisioning");
        }
    }
}
