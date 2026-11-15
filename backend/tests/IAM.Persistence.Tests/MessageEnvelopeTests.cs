using IAM.BuildingBlocks.Abstractions.Messaging;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>The message envelope's rules (DL-076): strict shape and bounds, strict reading, nothing printed.</summary>
public sealed class MessageEnvelopeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private sealed record Sample(Guid Id, string Code) : IIntegrationEvent
    {
        public static string MessageType => "test.sample.happened";

        public static int SchemaVersion => 2;
    }

    private sealed record OtherVersion(Guid Id, string Code) : IIntegrationEvent
    {
        public static string MessageType => "test.sample.happened";

        public static int SchemaVersion => 3;
    }

    private static MessageEnvelope Valid() =>
        MessageEnvelopes.Create(new Sample(Guid.CreateVersion7(), "secret-code"), "test", "corr-1", "cause:1", "test:key:1", T0);

    [Fact]
    public void AMessageRoundTripsThroughItsEnvelopeAndRow()
    {
        var envelope = Valid();
        envelope.Type.ShouldBe("test.sample.happened");
        envelope.SchemaVersion.ShouldBe(2);
        envelope.MessageId.Version.ShouldBe(7);
        envelope.Payload.ShouldStartWith("{");
        MessageEnvelopes.Read<Sample>(envelope).Code.ShouldBe("secret-code");
        OutboxMessageRow.From(envelope).ToEnvelope().ShouldBe(envelope);
    }

    [Fact]
    public void ReadingRefusesAnotherTypeVersionOrShape()
    {
        var envelope = Valid();
        Should.Throw<InvalidMessageException>(() => MessageEnvelopes.Read<OtherVersion>(envelope));
        Should.Throw<InvalidMessageException>(() => MessageEnvelopes.Read<Sample>(envelope with { Payload = """{"id":"01000000-0000-7000-8000-000000000000","code":"x","extra":1}""" }));
        Should.Throw<InvalidMessageException>(() => MessageEnvelopes.Read<Sample>(envelope with { Payload = "{not json" }));
        Should.Throw<InvalidMessageException>(() => MessageEnvelopes.Read<Sample>(envelope with { Type = "test.sample.other" }));
    }

    [Fact]
    public void EveryFieldIsCheckedAndBounded()
    {
        var envelope = Valid();
        MessageEnvelope[] bad =
        [
            envelope with { MessageId = Guid.Empty },
            envelope with { Type = "Test.Sample" },
            envelope with { Type = "single" },
            envelope with { Type = "test." + new string('a', 100) },
            envelope with { SchemaVersion = 0 },
            envelope with { SchemaVersion = 1001 },
            envelope with { Source = "Workflow" },
            envelope with { Source = "" },
            envelope with { CorrelationId = " corr" },
            envelope with { CorrelationId = new string('c', 101) },
            envelope with { CausationId = "cause 1" },
            envelope with { IdempotencyKey = "" },
            envelope with { IdempotencyKey = "key/with/slash" },
            envelope with { IdempotencyKey = new string('k', 201) },
            envelope with { OccurredAtUtc = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.FromHours(1)) },
            envelope with { Payload = "" },
            envelope with { Payload = "[1]" },
            envelope with { Payload = "{" + new string(' ', MessageEnvelopes.MaxPayloadLength) + "}" },
        ];
        foreach (var e in bad)
        {
            Should.Throw<InvalidMessageException>(() => MessageEnvelopes.Validate(e), e.ToString());
            Should.Throw<InvalidMessageException>(() => OutboxMessageRow.From(e));
        }

        MessageEnvelopes.Validate(envelope with { CausationId = null });
    }

    [Fact]
    public void AnEnvelopeNeverPrintsItsPayload()
    {
        var envelope = Valid();
        envelope.ToString().ShouldNotContain("secret-code");
        envelope.ToString().ShouldContain("test.sample.happened");
    }
}
