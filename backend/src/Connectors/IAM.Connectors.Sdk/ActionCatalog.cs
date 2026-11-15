namespace IAM.Connectors.Sdk;

/// <summary>What an action works on (spec 2.4).</summary>
public enum ActionGroup
{
    Accounts = 1,
    EntitlementsOnAnAccount = 2,
    MembersOfAnEntitlement = 3,
    EntitlementDefinitions = 4,
    Credentials = 5,
}

/// <summary>
/// One catalog action (spec "the master list of actions", 2.4; DL-080). <see cref="Target"/> is the object type the action
/// changes; <see cref="Related"/> the other object it needs, if any (the entitlement to add, the member to add). A
/// <see cref="Destructive"/> or <see cref="Sensitive"/> action is off unless the connector's settings switch it on (see
/// <see cref="Gated"/>); a credential action that sets a password takes a vault reference, never the password
/// (<see cref="NeedsSecretReference"/>).
/// </summary>
public sealed record ActionDefinition(
    string Code,
    ActionGroup Group,
    string Target,
    string? Related,
    bool Destructive,
    bool NeedsSecretReference,
    bool Sensitive = false)
{
    /// <summary>Off until the connector's settings switch it on, one by one (DL-080f, DL-083d).</summary>
    public bool Gated => Destructive || Sensitive;
}

/// <summary>Object types the catalog's actions work on. A connector may declare more for discovery.</summary>
public static class ObjectTypes
{
    public const string Account = "account";
    public const string Entitlement = "entitlement";
    public const string Container = "container";
}

/// <summary>
/// The action catalog (spec: "one master list of actions … nothing in the product has a hard-coded list of actions";
/// DL-080). Codes are stored and never renamed; a connector declares which it supports, and an application decides which
/// it allows and how (a later stage).
/// </summary>
public static class ActionCatalog
{
    public const string AccountCreate = "account.create";
    public const string AccountUpdate = "account.update";
    public const string AccountEnable = "account.enable";
    public const string AccountDisable = "account.disable";
    public const string AccountDelete = "account.delete";
    public const string AccountWriteOwner = "account.write_owner";
    public const string EntitlementAdd = "account.entitlement.add";
    public const string EntitlementRemove = "account.entitlement.remove";
    public const string MemberAdd = "entitlement.member.add";
    public const string MemberRemove = "entitlement.member.remove";
    public const string EntitlementCreate = "entitlement.create";
    public const string EntitlementDelete = "entitlement.delete";
    public const string PasswordChange = "credential.password.change";
    public const string PasswordReset = "credential.password.reset";
    public const string AccountUnlock = "credential.unlock";

    private static readonly ActionDefinition[] Definitions =
    [
        new(AccountCreate, ActionGroup.Accounts, ObjectTypes.Account, ObjectTypes.Container, Destructive: false, NeedsSecretReference: false),
        new(AccountUpdate, ActionGroup.Accounts, ObjectTypes.Account, null, Destructive: false, NeedsSecretReference: false),
        new(AccountEnable, ActionGroup.Accounts, ObjectTypes.Account, null, Destructive: false, NeedsSecretReference: false),
        new(AccountDisable, ActionGroup.Accounts, ObjectTypes.Account, null, Destructive: false, NeedsSecretReference: false),
        new(AccountDelete, ActionGroup.Accounts, ObjectTypes.Account, null, Destructive: true, NeedsSecretReference: false),
        new(AccountWriteOwner, ActionGroup.Accounts, ObjectTypes.Account, ObjectTypes.Account, Destructive: false, NeedsSecretReference: false),
        new(EntitlementAdd, ActionGroup.EntitlementsOnAnAccount, ObjectTypes.Account, ObjectTypes.Entitlement, Destructive: false, NeedsSecretReference: false),
        new(EntitlementRemove, ActionGroup.EntitlementsOnAnAccount, ObjectTypes.Account, ObjectTypes.Entitlement, Destructive: false, NeedsSecretReference: false),
        new(MemberAdd, ActionGroup.MembersOfAnEntitlement, ObjectTypes.Entitlement, ObjectTypes.Account, Destructive: false, NeedsSecretReference: false),
        new(MemberRemove, ActionGroup.MembersOfAnEntitlement, ObjectTypes.Entitlement, ObjectTypes.Account, Destructive: false, NeedsSecretReference: false),
        new(EntitlementCreate, ActionGroup.EntitlementDefinitions, ObjectTypes.Entitlement, ObjectTypes.Container, Destructive: false, NeedsSecretReference: false),
        new(EntitlementDelete, ActionGroup.EntitlementDefinitions, ObjectTypes.Entitlement, null, Destructive: true, NeedsSecretReference: false),
        new(PasswordChange, ActionGroup.Credentials, ObjectTypes.Account, null, Destructive: false, NeedsSecretReference: true, Sensitive: true),
        new(PasswordReset, ActionGroup.Credentials, ObjectTypes.Account, null, Destructive: false, NeedsSecretReference: false, Sensitive: true),
        new(AccountUnlock, ActionGroup.Credentials, ObjectTypes.Account, null, Destructive: false, NeedsSecretReference: false),
    ];

    private static readonly Dictionary<string, ActionDefinition> ByCode = Definitions.ToDictionary(d => d.Code, StringComparer.Ordinal);

    public static IReadOnlyList<ActionDefinition> All => Definitions;

    public static ActionDefinition? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var definition) ? definition : null;
}
