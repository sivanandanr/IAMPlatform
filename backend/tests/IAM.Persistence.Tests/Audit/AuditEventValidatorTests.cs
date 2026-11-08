using IAM.Modules.Audit.Application;
using IAM.Modules.Audit.Contracts;
using Shouldly;

namespace IAM.Persistence.Tests.Audit;

/// <summary>S1-07 audit record validation, masking and hashing (ADR-0018). No database.</summary>
public sealed class AuditEventValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid FixedId = Guid.Parse("01965f3a-0000-7000-8000-000000000001");
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal) { "parity.item.created" };

    internal static AuditEventRecord ValidRecord(IReadOnlyList<AuditPayloadField>? payload = null) => new()
    {
        Action = "parity.item.created",
        TargetType = "parity.item",
        TargetId = "item-1",
        Outcome = AuditOutcome.Succeeded,
        OccurredAtUtc = Now.AddSeconds(-1),
        CorrelationId = "corr-123",
        Payload = payload ?? [],
    };

    private static AuditPayloadField Plain(string name, string? value) => new(name, value, AuditFieldClass.Plain);

    private static ValidatedAuditEvent Validate(AuditEventRecord record, string? actor = "user-1") =>
        AuditEventValidator.Validate(record, actor, Actions, FixedId, Now);

    [Fact]
    public void AValidRecordIsAccepted()
    {
        var row = Validate(ValidRecord());
        row.ActorId.ShouldBe("user-1");
        row.Outcome.ShouldBe("Succeeded");
        row.PayloadCanonical.ShouldBe("{}");
        row.RowHash.ShouldMatch("^[0-9a-f]{64}$");
        row.RowHash.ShouldBe(AuditEventValidator.ComputeRowHash(row));
    }

    [Fact]
    public void TheRowHashCoversEveryStoredField()
    {
        var baseline = Validate(ValidRecord());
        Validate(ValidRecord()).RowHash.ShouldBe(baseline.RowHash, "the row hash is deterministic");

        var variants = new[]
        {
            Validate(ValidRecord(), actor: "user-2"),
            Validate(ValidRecord() with { Outcome = AuditOutcome.Denied }),
            Validate(ValidRecord() with { TargetId = "item-2" }),
            Validate(ValidRecord() with { OccurredAtUtc = Now.AddSeconds(-2) }),
            Validate(ValidRecord() with { Reason = "why" }),
            Validate(ValidRecord() with { CorrelationId = "corr-124" }),
            Validate(ValidRecord() with { SchemaVersion = 2 }),
            Validate(ValidRecord([Plain("company", "0100")])),
            AuditEventValidator.Validate(ValidRecord(), "user-1", Actions, Guid.CreateVersion7(), Now),
            AuditEventValidator.Validate(ValidRecord(), "user-1", Actions, FixedId, Now.AddTicks(10)),
        };

        foreach (var variant in variants)
        {
            variant.RowHash.ShouldNotBe(baseline.RowHash);
        }

        (baseline with { ActorId = "someone-else" }).RowHash.ShouldBe(baseline.RowHash, "the stored hash does not follow an edit");
        AuditEventValidator.ComputeRowHash(baseline with { ActorId = "someone-else" }).ShouldNotBe(baseline.RowHash, "so an edited row no longer matches its hash");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingActorIsRejected(string? actor) =>
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord(), actor)).Field.ShouldBe("ActorId");

    [Fact]
    public void AnUnregisteredActionIsRejected() =>
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { Action = "parity.item.deleted" })).Field.ShouldBe("Action");

    [Theory]
    [InlineData("Parity.item.created")]
    [InlineData("parity")]
    [InlineData("parity.item.created ")]
    [InlineData("parity..created")]
    [InlineData("parity.item;drop")]
    public void MalformedActionCodesAreRejected(string action) =>
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { Action = action })).Field.ShouldBe("Action");

    [Theory]
    [InlineData("corr id")]
    [InlineData("corr\nid")]
    [InlineData("")]
    public void MalformedCorrelationIdsAreRejected(string correlationId) =>
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { CorrelationId = correlationId })).Field.ShouldBe("CorrelationId");

    [Fact]
    public void ControlCharactersAreRejectedButReasonsMayHaveLineBreaks()
    {
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { TargetId = "item\u0000" })).Field.ShouldBe("TargetId");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { Reason = "a\u001b[31m" })).Field.ShouldBe("Reason");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { Reason = "a\tb" })).Field.ShouldBe("Reason");
        Validate(ValidRecord() with { Reason = "line one\r\nline two" }).Reason.ShouldBe("line one\r\nline two");
    }

    [Theory]
    [InlineData("item\u202E1")]
    [InlineData("item\u200B1")]
    [InlineData("item\uFEFF1")]
    [InlineData("item\x20281")]
    [InlineData("item\x20291")]
    public void InvisibleFormattingCharactersAreRejected(string targetId) =>
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { TargetId = targetId })).Field.ShouldBe("TargetId");

    [Fact]
    public void OverlongFieldsAreRejected()
    {
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { TargetId = new string('x', 201) })).Field.ShouldBe("TargetId");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { Reason = new string('x', 1001) })).Field.ShouldBe("Reason");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord(), new string('x', 201))).Field.ShouldBe("ActorId");
    }

    [Fact]
    public void TimestampsMustBeUtcAndNotInTheFuture()
    {
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { OccurredAtUtc = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.FromHours(2)) }));
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { OccurredAtUtc = Now.AddMinutes(6) }));
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { OccurredAtUtc = default }));
        Validate(ValidRecord() with { OccurredAtUtc = Now.AddMinutes(4) }).OccurredAtUtc.ShouldBe(Now.AddMinutes(4));
    }

    [Theory]
    [InlineData((AuditOutcome)0)]
    [InlineData((AuditOutcome)99)]
    public void UndefinedOutcomesAreRejected(AuditOutcome outcome) =>
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { Outcome = outcome })).Field.ShouldBe("Outcome");

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("abc")]
    public void PolicyHashesMustBeLowerCaseSha256Hex(string hash) =>
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { PolicyVersionHash = hash })).Field.ShouldBe("PolicyVersionHash");

    [Fact]
    public void SensitiveValuesAreMaskedBeforeHashing()
    {
        var row = Validate(ValidRecord([
            new AuditPayloadField("employeeNumber", "00012345", AuditFieldClass.Sensitive),
            Plain("company", "0100"),
        ]));

        row.PayloadCanonical.ShouldBe("{\"company\":\"0100\",\"employeeNumber\":\"[masked]\"}");
        row.PayloadCanonical.ShouldNotContain("00012345");
        row.RowHash.ShouldBe(AuditEventValidator.ComputeRowHash(row));
    }

    [Fact]
    public void ThePayloadIsCanonicalWhateverTheFieldOrder()
    {
        var first = Validate(ValidRecord([Plain("b", "2"), Plain("a", "1")]));
        var second = Validate(ValidRecord([Plain("a", "1"), Plain("b", "2")]));
        first.PayloadCanonical.ShouldBe(second.PayloadCanonical);
        first.RowHash.ShouldBe(second.RowHash);
    }

    [Fact]
    public void StringCodesKeepLeadingZeros() =>
        Validate(ValidRecord([Plain("eeSubgroup", "01")])).PayloadCanonical.ShouldBe("{\"eeSubgroup\":\"01\"}");

    [Theory]
    [InlineData("password")]
    [InlineData("newPassword")]
    [InlineData("passphrase")]
    [InlineData("accessToken")]
    [InlineData("refresh_token")]
    [InlineData("clientSecret")]
    [InlineData("apiKey")]
    [InlineData("api.key")]
    [InlineData("private.key")]
    [InlineData("session.id")]
    [InlineData("connection.string")]
    [InlineData("Authorization")]
    [InlineData("samlAssertion")]
    [InlineData("pin")]
    [InlineData("user.otp")]
    public void SecretLikeFieldNamesAreRejectedEvenWhenMarkedSensitive(string name)
    {
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([Plain(name, "x")])));
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([new AuditPayloadField(name, "x", AuditFieldClass.Sensitive)])));
    }

    [Theory]
    [InlineData("shipping")]
    [InlineData("mapping")]
    [InlineData("footprint")]
    public void OrdinaryNamesContainingShortSecretWordsAreAllowed(string name) =>
        Validate(ValidRecord([Plain(name, "x")])).PayloadCanonical.ShouldContain(name);

    [Theory]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJl")]
    [InlineData("Bearer abcdefghijklmnop")]
    [InlineData("  bearer abcdefghijklmnop")]
    [InlineData("header was Basic dXNlcjpwYXNzd29yZA==")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("Server=db;Password=hunter2;")]
    [InlineData("DefaultEndpointsProtocol=https;AccountKey=abc;")]
    [InlineData("https://acct.blob.core.windows.net/c?sv=2024&sig=abc")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    public void CredentialShapedValuesAreRejectedInEveryFreeTextField(string value)
    {
        var inPayload = Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([Plain("note", value)])));
        inPayload.Message.ShouldNotContain(value, Case.Sensitive, "the rejected value must not be echoed into logs");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { Reason = value })).Field.ShouldBe("Reason");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { TargetId = value })).Field.ShouldBe("TargetId");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { PolicyVersionRef = value })).Field.ShouldBe("PolicyVersionRef");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { EffectiveActorId = value })).Field.ShouldBe("EffectiveActorId");
    }

    [Theory]
    [InlineData("basic access requested")]
    [InlineData("password reset requested by the user")]
    public void OrdinaryWordsAreNotMistakenForCredentials(string reason) =>
        Validate(ValidRecord() with { Reason = reason }).Reason.ShouldBe(reason);

    [Fact]
    public void PayloadSizeAndShapeAreBounded()
    {
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord(
            Enumerable.Range(0, 65).Select(i => Plain($"f{i}", "v")).ToList())));
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([Plain("note", new string('x', 2001))])));
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([Plain("a", "1"), Plain("a", "2")])));
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([Plain("bad name", "1")])));
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([new AuditPayloadField("x", "1", (AuditFieldClass)9)])));
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord(
            Enumerable.Range(0, 10).Select(i => Plain($"f{i}", new string('x', 1900))).ToList())));
    }

    [Fact]
    public void UnicodeIsEscapedDeterministically()
    {
        var row = Validate(ValidRecord([Plain("name", "Zoë 漢字")]));
        row.PayloadCanonical.ShouldBe("{\"name\":\"Zo\\u00EB \\u6F22\\u5B57\"}");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([Plain("name", "\ud800x")])));
    }

    /// <summary>.NET "$" also matches before a final line break; every pattern now ends with "\z" (S1-08 review).</summary>
    [Fact]
    public void TrailingLineBreaksAreRejectedInEveryCode()
    {
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { Action = "parity.item.created\n" })).Field.ShouldBe("Action");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { TargetType = "parity.item\n" })).Field.ShouldBe("TargetType");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { CorrelationId = "corr-123\n" })).Field.ShouldBe("CorrelationId");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { CausationId = "cause-1\r\n" })).Field.ShouldBe("CausationId");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord() with { PolicyVersionHash = new string('a', 64) + "\n" }))
            .Field.ShouldBe("PolicyVersionHash");
        Should.Throw<AuditRecordRejectedException>(() => Validate(ValidRecord([Plain("company\n", "0100")])));
        AuditEventValidator.IsValidActionCode("parity.item.created\n").ShouldBeFalse();
    }
}
