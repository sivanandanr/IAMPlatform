using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace IAM.Modules.Catalog.Domain.Applications;

/// <summary>
/// One application in the catalog (spec Module 2.2-2.3, IAM-FR-010, AR-2, DL-066). Its <see cref="Id"/> and <see cref="Key"/>
/// never change. Its display name can be changed by someone who manages the catalog. Its governed settings (owner and
/// application roles) live in numbered <see cref="Versions"/>: at most one draft and at most one active. A draft becomes
/// active only through a request approved by a second person, which supersedes the previously active version. The
/// application counts as active once it has an active version.
/// </summary>
public sealed class Application
{
    private readonly List<ApplicationSettingsVersion> _versions = [];

    private Application()
    {
        Key = string.Empty;
        DisplayName = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; }

    public string DisplayName { get; private set; }

    public IReadOnlyList<ApplicationSettingsVersion> Versions => _versions;

    public ApplicationSettingsVersion? CurrentDraft => _versions.SingleOrDefault(v => v.State == SettingsVersionState.Draft);

    public ApplicationSettingsVersion? CurrentActive => _versions.SingleOrDefault(v => v.State == SettingsVersionState.Active);

    /// <summary>Registers a new application with draft settings version 1. It is not active until that draft is approved.</summary>
    public static Application Register(string key, string displayName, ApplicationSettings settings)
    {
        CatalogRules.ValidateKey(key, "Key");
        CatalogRules.ValidateDisplayName(displayName, CatalogRules.DisplayNameMaxLength, "DisplayName");
        CatalogRules.ValidateSettings(settings);
        var application = new Application { Id = Guid.CreateVersion7(), Key = key, DisplayName = displayName };
        application._versions.Add(ApplicationSettingsVersion.CreateDraft(application, 1, settings));
        return application;
    }

    /// <summary>A name is not access (CLAUDE.md rule 11): changing it needs no second person. Returns false if unchanged.</summary>
    public bool Rename(string displayName)
    {
        CatalogRules.ValidateDisplayName(displayName, CatalogRules.DisplayNameMaxLength, "DisplayName");
        if (string.Equals(DisplayName, displayName, StringComparison.Ordinal))
        {
            return false;
        }

        DisplayName = displayName;
        return true;
    }

    /// <summary>Replaces the draft settings. Returns false if nothing changed.</summary>
    public bool UpdateDraft(ApplicationSettings settings)
    {
        CatalogRules.ValidateSettings(settings);
        var draft = CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        return draft.Apply(Key, settings);
    }

    /// <summary>
    /// Starts the next settings version as a draft. Refused while a draft exists. Superseded versions need not be loaded, so
    /// the caller passes the highest stored version number. An application always has a draft or an active version (the
    /// first draft only ever leaves Draft by being activated); anything else is a damaged record.
    /// </summary>
    public ApplicationSettingsVersion StartNewDraft(ApplicationSettings settings, int highestVersionNumber)
    {
        CatalogRules.ValidateSettings(settings);
        if (CurrentDraft is not null)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.DraftExists);
        }

        if (CurrentActive is null || highestVersionNumber < _versions.Max(v => v.VersionNumber))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        var next = ApplicationSettingsVersion.CreateDraft(this, highestVersionNumber + 1, settings);
        _versions.Add(next);
        return next;
    }

    /// <summary>
    /// Activates exactly the draft the request named (same number and content hash) and supersedes the active version.
    /// Only an approved request calls this (<see cref="ApplicationSettingsRequest.Approve"/>).
    /// </summary>
    internal void Activate(int versionNumber, string contentHash, Guid requestId)
    {
        var draft = RequireActivatable(versionNumber, contentHash);
        CurrentActive?.MarkSuperseded();
        draft.MarkActive(requestId);
    }

    /// <summary>Checks, without changing anything, that the named draft is still there unchanged and has no empty required role.</summary>
    internal ApplicationSettingsVersion RequireActivatable(int versionNumber, string contentHash)
    {
        var draft = CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        if (draft.VersionNumber != versionNumber || !string.Equals(draft.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.DraftChanged);
        }

        return draft.HasEmptyRequiredRole ? throw new ApplicationRefusedException(ApplicationRefusal.RequiredRoleEmpty) : draft;
    }
}

/// <summary>
/// One numbered version of an application's owner and roles. A draft can change; active and superseded versions never do
/// (database triggers refuse it, and refuse role row changes under them).
/// </summary>
public sealed class ApplicationSettingsVersion
{
    /// <summary>Part of the hashed text, so the hash layout can change later without ambiguity.</summary>
    public const string ContentHashLayout = "iam.catalog.application-settings.v1";

    private readonly List<ApplicationSettingsRole> _roles = [];

    private ApplicationSettingsVersion()
    {
        Owner = string.Empty;
        ContentHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ApplicationId { get; private set; }

    public int VersionNumber { get; private set; }

    public SettingsVersionState State { get; private set; }

    /// <summary>The single application owner, a person (AR-2.4).</summary>
    public string Owner { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the canonical settings (see <see cref="ComputeContentHash"/>).</summary>
    public string ContentHash { get; private set; }

    /// <summary>The approved request that activated this version; none while it is a draft.</summary>
    public Guid? ActivatedByRequestId { get; private set; }

    public IReadOnlyList<ApplicationSettingsRole> Roles => _roles;

    /// <summary>Whether <paramref name="person"/> is named as a person holding a role in this version (DL-067a).</summary>
    public bool NamesPersonHolder(string? person) =>
        person is not null && _roles.Any(r => r.HolderKind == RoleHolderKind.Person && string.Equals(r.HolderId, person, StringComparison.Ordinal));

    /// <summary>A required role with nobody holding it blocks activation (AR-2.3, DL-052).</summary>
    public bool HasEmptyRequiredRole => _roles.Any(r => r.Requirement == RoleRequirement.Required && r.HolderId is null);

    internal static ApplicationSettingsVersion CreateDraft(Application application, int versionNumber, ApplicationSettings settings)
    {
        var version = new ApplicationSettingsVersion
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = application.Id,
            VersionNumber = versionNumber,
            State = SettingsVersionState.Draft,
        };
        version.Apply(application.Key, settings);
        return version;
    }

    /// <summary>
    /// Lower-case hex SHA-256 over the UTF-8 compact JSON array
    /// <c>[layout, key, versionNumber, owner, [[roleTypeId, requirement, holderKind, holderId] sorted by roleTypeId]]</c>,
    /// enum values by name and an empty holder as nulls.
    /// </summary>
    public static string ComputeContentHash(string key, int versionNumber, ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(settings);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(ContentHashLayout);
            writer.WriteStringValue(key);
            writer.WriteNumberValue(versionNumber);
            writer.WriteStringValue(settings.Owner);
            writer.WriteStartArray();
            foreach (var role in settings.Roles.OrderBy(r => r.RoleTypeId.ToString("D"), StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(role.RoleTypeId.ToString("D"));
                writer.WriteStringValue(role.Requirement.ToString());
                if (role.HolderKind is { } kind)
                {
                    writer.WriteStringValue(kind.ToString());
                    writer.WriteStringValue(role.HolderId);
                }
                else
                {
                    writer.WriteNullValue();
                    writer.WriteNullValue();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>The current content as settings.</summary>
    public ApplicationSettings ToSettings() => new(
        Owner,
        _roles.OrderBy(r => r.RoleTypeId.ToString("D"), StringComparer.Ordinal)
            .Select(r => new ApplicationRoleSetting(r.RoleTypeId, r.Requirement, r.HolderKind, r.HolderId))
            .ToList());

    internal bool Apply(string key, ApplicationSettings settings)
    {
        if (State != SettingsVersionState.Draft)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        }

        var hash = ComputeContentHash(key, VersionNumber, settings);
        if (hash == ContentHash)
        {
            return false;
        }

        Owner = settings.Owner;
        ContentHash = hash;

        // Change role rows by difference, so unchanged rows keep their identity.
        var wanted = settings.Roles.ToDictionary(r => r.RoleTypeId);
        _roles.RemoveAll(r => !wanted.ContainsKey(r.RoleTypeId));
        foreach (var role in settings.Roles)
        {
            var existing = _roles.SingleOrDefault(r => r.RoleTypeId == role.RoleTypeId);
            if (existing is null)
            {
                _roles.Add(new ApplicationSettingsRole(Id, role));
            }
            else
            {
                existing.Set(role);
            }
        }

        return true;
    }

    internal void MarkActive(Guid requestId)
    {
        State = SettingsVersionState.Active;
        ActivatedByRequestId = requestId;
    }

    /// <summary>The only change an active version allows (the database refuses any other).</summary>
    internal void MarkSuperseded()
    {
        if (State != SettingsVersionState.Active)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        State = SettingsVersionState.Superseded;
    }
}

/// <summary>One application role in a settings version. Stored as a row (AR-2.1: a catalog table, not fixed columns).</summary>
public sealed class ApplicationSettingsRole
{
    internal ApplicationSettingsRole(Guid settingsVersionId, ApplicationRoleSetting setting)
    {
        SettingsVersionId = settingsVersionId;
        RoleTypeId = setting.RoleTypeId;
        Set(setting);
    }

    private ApplicationSettingsRole()
    {
    }

    public Guid SettingsVersionId { get; private set; }

    public Guid RoleTypeId { get; private set; }

    public RoleRequirement Requirement { get; private set; }

    public RoleHolderKind? HolderKind { get; private set; }

    public string? HolderId { get; private set; }

    internal void Set(ApplicationRoleSetting setting)
    {
        Requirement = setting.Requirement;
        HolderKind = setting.HolderKind;
        HolderId = setting.HolderId;
    }
}
