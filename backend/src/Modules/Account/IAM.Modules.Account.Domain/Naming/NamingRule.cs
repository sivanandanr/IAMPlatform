using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.Modules.Account.Domain.Accounts;

namespace IAM.Modules.Account.Domain.Naming;

/// <summary>
/// The only things a naming rule can do to an attribute value (DL-089c). A fixed list: no expressions, no scripts (CLAUDE.md
/// rule 8).
/// </summary>
public enum NameTransform
{
    Lower = 1,
    Upper = 2,

    /// <summary>Keeps the first <see cref="NamePart.Length"/> characters.</summary>
    First = 3,

    /// <summary>Replaces accented letters with their plain letters (é becomes e) and drops other combining marks.</summary>
    RemoveAccents = 4,

    /// <summary>Keeps only ASCII letters and digits.</summary>
    LettersDigitsOnly = 5,
}

/// <summary>
/// One part of a name: literal text, or a registered attribute's value with transforms applied in order. A literal is kept as
/// it is; it must itself be name characters.
/// </summary>
public sealed record NamePart(string? Literal, string? Attribute, IReadOnlyList<NameTransform> Transforms, int? Length)
{
    public static NamePart Text(string literal) => new(literal, null, [], null);

    public static NamePart From(string attribute, params NameTransform[] transforms) => new(null, attribute, transforms, null);

    public static NamePart FromFirst(string attribute, int length, params NameTransform[] transforms) => new(null, attribute, [.. transforms, NameTransform.First], length);
}

/// <summary>The content of a naming rule version: its parts and the longest name it may produce.</summary>
public sealed record NamingTemplate(IReadOnlyList<NamePart> Parts, int MaxLength);

/// <summary>Why a name couldn't be made: every one fails closed, and a person then gives the name (slice 3).</summary>
public enum NamingFailure
{
    /// <summary>An attribute the rule uses has no value in the snapshot.</summary>
    MissingAttribute = 1,

    /// <summary>The result is empty, too long, or not a valid name.</summary>
    InvalidName = 2,
}

/// <summary>The outcome of evaluating a naming rule: a name, or why there is none.</summary>
public sealed record NamingResult(string? Name, NamingFailure? Failure)
{
    public bool Succeeded => Name is not null;
}

/// <summary>Validation, the canonical form, the content hash and evaluation of naming templates.</summary>
public static partial class NamingTemplates
{
    public const int MaxParts = 10;
    public const int MaxLiteralLength = 20;
    public const int MaxTransforms = 5;
    public const int MaxFirstLength = 64;
    public const int MaxStoredLength = 4000;

    /// <summary>Part of the hashed text, so the layout can change later without ambiguity.</summary>
    public const string ContentHashLayout = "iam.account.naming-rule.v1";

    public static void Validate(NamingTemplate? template)
    {
        if (template is null)
        {
            throw new AccountRejectedException("Template", "is required");
        }

        if (template.Parts is null || template.Parts.Count is 0 or > MaxParts)
        {
            throw new AccountRejectedException("Parts", $"must be 1-{MaxParts} parts");
        }

        if (template.MaxLength is < 1 or > AccountRules.NameMaxLength)
        {
            throw new AccountRejectedException("MaxLength", $"must be 1-{AccountRules.NameMaxLength}");
        }

        if (!template.Parts.Any(p => p?.Attribute is not null))
        {
            throw new AccountRejectedException("Parts", "must use at least one attribute");
        }

        foreach (var part in template.Parts)
        {
            ValidatePart(part);
        }
    }

    /// <summary>The attribute keys a template uses, each once.</summary>
    public static IReadOnlyList<string> Attributes(NamingTemplate template) =>
        [.. template.Parts.Where(p => p.Attribute is not null).Select(p => p.Attribute!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The stored form: compact JSON <c>{"maxLength":n,"parts":[{"literal":"x"} or {"attribute":"k","transforms":["Lower"],"length":n}]}</c>,
    /// with enum values by name. Reading it back gives the same text, so it is also what the hash covers.
    /// </summary>
    public static string ToStored(NamingTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("maxLength", template.MaxLength);
            writer.WriteStartArray("parts");
            foreach (var part in template.Parts)
            {
                writer.WriteStartObject();
                if (part.Literal is not null)
                {
                    writer.WriteString("literal", part.Literal);
                }
                else
                {
                    writer.WriteString("attribute", part.Attribute);
                    writer.WriteStartArray("transforms");
                    foreach (var transform in part.Transforms)
                    {
                        writer.WriteStringValue(transform.ToString());
                    }

                    writer.WriteEndArray();
                    if (part.Length is { } length)
                    {
                        writer.WriteNumber("length", length);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads a stored template back; anything that isn't exactly what <see cref="ToStored"/> writes is a damaged record.</summary>
    public static NamingTemplate FromStored(string? stored)
    {
        try
        {
            if (stored is null || stored.Length > MaxStoredLength)
            {
                throw new FormatException();
            }

            using var json = JsonDocument.Parse(stored, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            var parts = new List<NamePart>();
            foreach (var element in root.GetProperty("parts").EnumerateArray())
            {
                if (element.TryGetProperty("literal", out var literal))
                {
                    parts.Add(NamePart.Text(literal.GetString()!));
                    continue;
                }

                var transforms = element.GetProperty("transforms").EnumerateArray()
                    .Select(t => Enum.GetNames<NameTransform>().Contains(t.GetString(), StringComparer.Ordinal) ? Enum.Parse<NameTransform>(t.GetString()!) : throw new FormatException())
                    .ToList();
                int? length = element.TryGetProperty("length", out var l) ? l.GetInt32() : null;
                parts.Add(new NamePart(null, element.GetProperty("attribute").GetString(), transforms, length));
            }

            var template = new NamingTemplate(parts, root.GetProperty("maxLength").GetInt32());
            Validate(template);
            return string.Equals(ToStored(template), stored, StringComparison.Ordinal) ? template : throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or AccountRejectedException)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord, ex);
        }
    }

    /// <summary>Lower-case hex SHA-256 over the UTF-8 compact JSON array <c>[layout, applicationId, accountType, versionNumber, storedTemplate]</c>.</summary>
    public static string ComputeContentHash(Guid applicationId, AccountType type, int versionNumber, NamingTemplate template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(ContentHashLayout);
            writer.WriteStringValue(applicationId.ToString("D"));
            writer.WriteStringValue(type.ToString());
            writer.WriteNumberValue(versionNumber);
            writer.WriteStringValue(ToStored(template));
            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>
    /// Makes a name from attribute values. A missing or empty value, an empty result, a result longer than the maximum or a
    /// result that isn't a valid name all fail closed: no name is guessed or shortened to fit.
    /// </summary>
    public static NamingResult Evaluate(NamingTemplate template, IReadOnlyDictionary<string, string?> attributes)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(attributes);
        var name = new StringBuilder();
        foreach (var part in template.Parts)
        {
            if (part.Literal is not null)
            {
                name.Append(part.Literal);
                continue;
            }

            if (!attributes.TryGetValue(part.Attribute!, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return new NamingResult(null, NamingFailure.MissingAttribute);
            }

            foreach (var transform in part.Transforms)
            {
                value = Apply(transform, value, part.Length);
            }

            name.Append(value);
        }

        var result = name.ToString();
        return result.Length <= template.MaxLength && AccountRules.IsName(result)
            ? new NamingResult(result, null)
            : new NamingResult(null, NamingFailure.InvalidName);
    }

    private static string Apply(NameTransform transform, string value, int? length) => transform switch
    {
        NameTransform.Lower => value.ToLowerInvariant(),
        NameTransform.Upper => value.ToUpperInvariant(),
        NameTransform.First => new string([.. value.EnumerateRunes().Take(length!.Value).SelectMany(r => r.ToString())]),
        NameTransform.RemoveAccents => RemoveAccents(value),
        NameTransform.LettersDigitsOnly => new string([.. value.Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')]),
        _ => throw new AccountRefusedException(AccountRefusal.InvalidRecord),
    };

    private static string RemoveAccents(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var kept = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                kept.Append(c);
            }
        }

        return kept.ToString().Normalize(NormalizationForm.FormC);
    }

    private static void ValidatePart(NamePart? part)
    {
        if (part is null || (part.Literal is null) == (part.Attribute is null))
        {
            throw new AccountRejectedException("Parts", "must each be literal text or an attribute");
        }

        if (part.Literal is not null)
        {
            if (part.Literal.Length is 0 or > MaxLiteralLength || !LiteralPattern().IsMatch(part.Literal) || part.Transforms is { Count: > 0 } || part.Length is not null)
            {
                throw new AccountRejectedException("Literal", $"must be 1-{MaxLiteralLength} letters, digits, dots, underscores or hyphens, without transforms");
            }

            return;
        }

        if (!AttributeKeyPattern().IsMatch(part.Attribute!))
        {
            throw new AccountRejectedException("Attribute", "must be an attribute key");
        }

        if (part.Transforms is null || part.Transforms.Count > MaxTransforms || part.Transforms.Any(t => !Enum.IsDefined(t)))
        {
            throw new AccountRejectedException("Transforms", $"must be at most {MaxTransforms} of Lower, Upper, First, RemoveAccents, LettersDigitsOnly");
        }

        var takesLength = part.Transforms.Contains(NameTransform.First);
        if (takesLength ? part.Length is not (>= 1 and <= MaxFirstLength) : part.Length is not null)
        {
            throw new AccountRejectedException("Length", $"must be 1-{MaxFirstLength} when First is used, and absent otherwise");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex LiteralPattern();

    /// <summary>The attribute catalog's key shape (lower-case code); the service also checks the attribute is published.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AttributeKeyPattern();
}

/// <summary>
/// The naming rule of one application for one account type (DL-089b, DL-089c): Primary or Secondary. Its content lives in
/// numbered versions, at most one draft and one active; a draft becomes active only through a request a second person
/// approves.
/// </summary>
public sealed class NamingRule
{
    private readonly List<NamingRuleVersion> _versions = [];

    private NamingRule()
    {
    }

    public Guid Id { get; private set; }

    public Guid ApplicationId { get; private set; }

    public AccountType AccountType { get; private set; }

    public IReadOnlyList<NamingRuleVersion> Versions => _versions;

    public NamingRuleVersion? CurrentDraft => _versions.SingleOrDefault(v => v.State == NamingRuleVersionState.Draft);

    public NamingRuleVersion? CurrentActive => _versions.SingleOrDefault(v => v.State == NamingRuleVersionState.Active);

    public static NamingRule Create(Guid applicationId, AccountType type, NamingTemplate template, string editor)
    {
        if (applicationId == Guid.Empty)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord);
        }

        AccountRules.ValidateType(type);
        if (type is not (AccountType.Primary or AccountType.Secondary))
        {
            throw new AccountRefusedException(AccountRefusal.NoNamingRuleForType);
        }

        NamingTemplates.Validate(template);
        var rule = new NamingRule { Id = Guid.CreateVersion7(), ApplicationId = applicationId, AccountType = type };
        rule._versions.Add(NamingRuleVersion.CreateDraft(rule, 1, template, editor));
        return rule;
    }

    /// <summary>Replaces the draft, or starts a new one after the active version. Returns false if nothing changed.</summary>
    public bool SetDraft(NamingTemplate template, int highestVersionNumber, string editor)
    {
        NamingTemplates.Validate(template);
        if (CurrentDraft is { } draft)
        {
            return draft.Apply(ApplicationId, AccountType, template, editor);
        }

        if (CurrentActive is null || highestVersionNumber < _versions.Max(v => v.VersionNumber))
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord);
        }

        _versions.Add(NamingRuleVersion.CreateDraft(this, highestVersionNumber + 1, template, editor));
        return true;
    }

    internal NamingRuleVersion RequireActivatable(int versionNumber, string contentHash)
    {
        var draft = CurrentDraft ?? throw new AccountRefusedException(AccountRefusal.NoDraft);
        return draft.VersionNumber == versionNumber && string.Equals(draft.ContentHash, contentHash, StringComparison.Ordinal)
            ? draft
            : throw new AccountRefusedException(AccountRefusal.DraftChanged);
    }

    internal void Activate(int versionNumber, string contentHash, Guid requestId)
    {
        var draft = RequireActivatable(versionNumber, contentHash);
        CurrentActive?.MarkSuperseded();
        draft.MarkActive(requestId);
    }
}

/// <summary>One numbered version of a naming rule. Active and superseded versions never change (database triggers refuse it).</summary>
public sealed class NamingRuleVersion
{
    private NamingRuleVersion()
    {
        Template = string.Empty;
        ContentHash = string.Empty;
        EditedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid NamingRuleId { get; private set; }

    public int VersionNumber { get; private set; }

    public NamingRuleVersionState State { get; private set; }

    /// <summary>The stored form (<see cref="NamingTemplates.ToStored"/>).</summary>
    public string Template { get; private set; }

    public string ContentHash { get; private set; }

    /// <summary>Who wrote this content; they can't be the second person.</summary>
    public string EditedBy { get; private set; }

    public Guid? ActivatedByRequestId { get; private set; }

    public NamingTemplate ToTemplate() => NamingTemplates.FromStored(Template);

    internal static NamingRuleVersion CreateDraft(NamingRule rule, int versionNumber, NamingTemplate template, string editor)
    {
        var version = new NamingRuleVersion { Id = Guid.CreateVersion7(), NamingRuleId = rule.Id, VersionNumber = versionNumber, State = NamingRuleVersionState.Draft };
        version.Apply(rule.ApplicationId, rule.AccountType, template, editor);
        return version;
    }

    internal bool Apply(Guid applicationId, AccountType type, NamingTemplate template, string editor)
    {
        if (State != NamingRuleVersionState.Draft)
        {
            throw new AccountRefusedException(AccountRefusal.NoDraft);
        }

        if (!SecondPersonText.IsPerson(editor))
        {
            throw new AccountRefusedException(AccountRefusal.NotAllowed);
        }

        var hash = NamingTemplates.ComputeContentHash(applicationId, type, VersionNumber, template);
        if (hash == ContentHash)
        {
            return false;
        }

        Template = NamingTemplates.ToStored(template);
        ContentHash = hash;
        EditedBy = editor;
        return true;
    }

    internal void MarkActive(Guid requestId)
    {
        State = NamingRuleVersionState.Active;
        ActivatedByRequestId = requestId;
    }

    internal void MarkSuperseded()
    {
        if (State != NamingRuleVersionState.Active)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord);
        }

        State = NamingRuleVersionState.Superseded;
    }
}

/// <summary>A request to activate one exact draft of a naming rule; decided by a second person who neither made the request nor wrote the draft.</summary>
public sealed class NamingRuleChangeRequest
{
    private readonly SecondPersonDecision _decision;

    private NamingRuleChangeRequest(Guid id, Guid namingRuleId, Guid versionId, int versionNumber, string contentHash, SecondPersonDecision decision)
    {
        Id = id;
        NamingRuleId = namingRuleId;
        VersionId = versionId;
        VersionNumber = versionNumber;
        ContentHash = contentHash;
        _decision = decision;
    }

    public Guid Id { get; }

    public Guid NamingRuleId { get; }

    public Guid VersionId { get; }

    public int VersionNumber { get; }

    public string ContentHash { get; }

    public string Maker => _decision.Maker;

    public string Reason => _decision.Reason;

    public DateTimeOffset ProposedAtUtc => _decision.ProposedAtUtc;

    public SecondPersonState State => _decision.State;

    public string? Checker => _decision.Checker;

    public string? DecisionReason => _decision.DecisionReason;

    public DateTimeOffset? DecidedAtUtc => _decision.DecidedAtUtc;

    public static NamingRuleChangeRequest Propose(Guid id, NamingRule rule, string maker, string? reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (id == Guid.Empty)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord);
        }

        var draft = rule.CurrentDraft ?? throw new AccountRefusedException(AccountRefusal.NoDraft);
        var decision = AccountSecondPerson.Shared(() => SecondPersonDecision.Propose(maker, reason, now));
        return new NamingRuleChangeRequest(id, rule.Id, draft.Id, draft.VersionNumber, draft.ContentHash, decision);
    }

    public static NamingRuleChangeRequest Restore(
        Guid id,
        Guid namingRuleId,
        Guid versionId,
        int versionNumber,
        string contentHash,
        string maker,
        string reason,
        DateTimeOffset proposedAtUtc,
        SecondPersonState state,
        string? checker,
        string? decisionReason,
        DateTimeOffset? decidedAtUtc)
    {
        if (id == Guid.Empty || namingRuleId == Guid.Empty || versionId == Guid.Empty || versionNumber < 1 || !AccountRules.IsHash(contentHash))
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord);
        }

        try
        {
            return new NamingRuleChangeRequest(id, namingRuleId, versionId, versionNumber, contentHash,
                SecondPersonDecision.Restore(maker, reason, proposedAtUtc, state, checker, decisionReason, decidedAtUtc));
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord, ex);
        }
    }

    public void RequireDecider(string checker) => AccountSecondPerson.Shared(() => _decision.RequireDecider(checker));

    public void Approve(string checker, string? reason, NamingRule rule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        RequireDecider(checker);
        if (rule.Id != NamingRuleId)
        {
            throw new AccountRefusedException(AccountRefusal.InvalidRecord);
        }

        var draft = rule.RequireActivatable(VersionNumber, ContentHash);
        if (string.Equals(draft.EditedBy, checker, StringComparison.Ordinal))
        {
            throw new AccountRefusedException(AccountRefusal.MakerCannotDecide);
        }

        AccountSecondPerson.Shared(() => SecondPersonDecision.RequireOptionalReason(reason));
        AccountSecondPerson.Shared(() => _decision.Approve(checker, reason, now));
        rule.Activate(VersionNumber, ContentHash, Id);
    }

    public void Reject(string checker, string? reason, DateTimeOffset now)
    {
        RequireDecider(checker);
        AccountSecondPerson.Shared(() => _decision.Reject(checker, reason, now));
    }

    public void Withdraw(string actor, string? reason, DateTimeOffset now) => AccountSecondPerson.Shared(() => _decision.Withdraw(actor, reason, now));
}
