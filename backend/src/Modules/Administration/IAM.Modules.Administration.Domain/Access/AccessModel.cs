using System.Globalization;
using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Administration.Domain.Access;

/// <summary>A role: a code, a display name and the actions it gives. A role that isn't <see cref="Assignable"/> can be held by nobody.</summary>
public sealed record Role(string Code, string Name, IReadOnlyList<string> Actions, bool Assignable = true);

/// <summary>Two roles one person may not hold together. Stored with the codes in ordinal order, so each pair has one form.</summary>
public sealed record IncompatiblePair(string First, string Second)
{
    public static IncompatiblePair Of(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? new IncompatiblePair(a, b) : new IncompatiblePair(b, a);
}

/// <summary>One person holding one role. The person is the identity provider's <c>sub</c> until a person directory exists (DL-059d).</summary>
public sealed record Assignment(string Subject, string RoleCode);

/// <summary>A change to who can do what. Only an approved <see cref="ChangeRequest"/> applies one (DL-016, DL-059b).</summary>
public abstract record AccessChange;

public sealed record AssignRole(string Subject, string RoleCode) : AccessChange;

public sealed record RemoveAssignment(string Subject, string RoleCode) : AccessChange;

/// <summary>Creates a role, or replaces its name and actions. Published roles never change in place: this makes a new model version.</summary>
public sealed record DefineRole(string Code, string Name, IReadOnlyList<string> Actions) : AccessChange;

public sealed record AddIncompatiblePair(string First, string Second) : AccessChange;

public sealed record RemoveIncompatiblePair(string First, string Second) : AccessChange;

/// <summary>
/// Who can do what, at one version. Immutable: every change makes a new model, so a decision can name the version it was
/// made against. Checks are made here and again on approval, against the model as it then is.
/// </summary>
public sealed class AccessModel
{
    public const string IamAdministrator = "iam-administrator";
    public const string IamManager = "iam-manager";
    public const string Auditor = "auditor";
    public const string PolicyAuthor = "policy-author";
    public const string PolicyChecker = "policy-checker";
    public const string BreakGlassCustodian = "break-glass-custodian";

    private readonly Dictionary<string, Role> _roles;
    private readonly HashSet<Assignment> _assignments;
    private readonly HashSet<IncompatiblePair> _pairs;

    private AccessModel(Dictionary<string, Role> roles, HashSet<Assignment> assignments, HashSet<IncompatiblePair> pairs, long version)
    {
        _roles = roles;
        _assignments = assignments;
        _pairs = pairs;
        Version = version;
    }

    /// <summary>Increases with every applied change. Nothing else changes it.</summary>
    public long Version { get; }

    public IReadOnlyList<Role> Roles => [.. _roles.Values.OrderBy(r => r.Code, StringComparer.Ordinal)];

    public IReadOnlyList<Assignment> Assignments => [.. _assignments.OrderBy(a => a.Subject, StringComparer.Ordinal).ThenBy(a => a.RoleCode, StringComparer.Ordinal)];

    public IReadOnlyList<IncompatiblePair> IncompatiblePairs => [.. _pairs.OrderBy(p => p.First, StringComparer.Ordinal).ThenBy(p => p.Second, StringComparer.Ordinal)];

    /// <summary>
    /// The default roles and pairs, with nobody assigned. Defaults are data, changeable later through a change request:
    /// IAM Administrator proposes, IAM Manager approves (spec: "the second person on sensitive changes") and escalates,
    /// Auditor only reads. Policy author manages attributes and Policy checker approves their publication (DL-064). The break-glass custodian is defined but can't be assigned
    /// until its own ADR exists (ADR-0008). Configuring and approving are separate (DL-057d), as are Administrator and
    /// Auditor (AR-1.9) and policy author and checker (ADR-0008).
    /// </summary>
    public static AccessModel Defaults()
    {
        Role[] roles =
        [
            new(IamAdministrator, "IAM Administrator", [ActionCatalog.RolesRead, ActionCatalog.ChangesRead, ActionCatalog.ChangesPropose]),
            new(IamManager, "IAM Manager", [ActionCatalog.RolesRead, ActionCatalog.ChangesRead, ActionCatalog.ChangesApprove, ActionCatalog.ApprovalsEscalate]),
            new(Auditor, "Auditor", [ActionCatalog.RolesRead, ActionCatalog.ChangesRead, ActionCatalog.AttributesRead]),
            new(PolicyAuthor, "Policy author", [ActionCatalog.AttributesRead, ActionCatalog.AttributesManage]),
            new(PolicyChecker, "Policy checker", [ActionCatalog.AttributesRead, ActionCatalog.AttributesApprove]),
            new(BreakGlassCustodian, "Break-glass custodian", [], Assignable: false),
        ];
        IncompatiblePair[] pairs =
        [
            IncompatiblePair.Of(IamAdministrator, IamManager),
            IncompatiblePair.Of(IamAdministrator, Auditor),
            IncompatiblePair.Of(PolicyAuthor, PolicyChecker),
        ];
        return new AccessModel(roles.ToDictionary(r => r.Code, Copy, StringComparer.Ordinal), [], [.. pairs], 0);
    }

    /// <summary>Rebuilds a stored model. Every rule is checked again, so a damaged store can't produce a model the rules forbid.</summary>
    public static AccessModel Load(IEnumerable<Role> roles, IEnumerable<Assignment> assignments, IEnumerable<IncompatiblePair> pairs, long version)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(pairs);
        var model = new AccessModel(new Dictionary<string, Role>(StringComparer.Ordinal), [], [], version);
        foreach (var role in roles)
        {
            ValidateRole(role);
            if (!model._roles.TryAdd(role.Code, Copy(role)))
            {
                throw new AccessRefusedException(AccessRefusal.InvalidRoleCode);
            }
        }

        foreach (var pair in pairs)
        {
            model.ValidatePair(pair?.First, pair?.Second);
            model._pairs.Add(IncompatiblePair.Of(pair!.First, pair.Second));
        }

        foreach (var assignment in assignments)
        {
            model.ValidateAssignment(assignment?.Subject, assignment?.RoleCode);
            model._assignments.Add(assignment!);
        }

        model.RequireLimits();
        model.RequireSeparatedDuties();
        return model;
    }

    /// <summary>
    /// The first two people, set once at deployment (DL-059e): an IAM Administrator to propose and a different IAM Manager
    /// to approve. Refused if anyone is already assigned, so it can't be used again as a way in.
    /// </summary>
    public AccessModel Bootstrap(string administrator, string manager)
    {
        if (_assignments.Count > 0)
        {
            throw new AccessRefusedException(AccessRefusal.AlreadyBootstrapped);
        }

        RequireSubject(administrator);
        RequireSubject(manager);
        if (string.Equals(administrator, manager, StringComparison.Ordinal))
        {
            throw new AccessRefusedException(AccessRefusal.SameBootstrapPerson);
        }

        var next = Next();
        next.Add(new AssignRole(administrator, IamAdministrator));
        next.Add(new AssignRole(manager, IamManager));
        next.RequireSeparatedDuties();
        next.RequireNoLockout();
        return next;
    }

    /// <summary>What this person may do now: their roles' actions plus the actions everyone signed in has. Unknown people get only those.</summary>
    public IReadOnlySet<string> ActionsOf(string subject)
    {
        var actions = new HashSet<string>(ActionCatalog.EveryoneActions, StringComparer.Ordinal);
        foreach (var assignment in _assignments.Where(a => string.Equals(a.Subject, subject, StringComparison.Ordinal)))
        {
            if (_roles.TryGetValue(assignment.RoleCode, out var role) && role.Assignable)
            {
                actions.UnionWith(role.Actions);
            }
        }

        return actions;
    }

    /// <summary>Default deny: an action not in the catalog is never allowed.</summary>
    public bool Can(string subject, string action) => ActionCatalog.IsKnown(action) && ActionsOf(subject).Contains(action);

    public IReadOnlyList<string> RolesOf(string subject) =>
        [.. _assignments.Where(a => string.Equals(a.Subject, subject, StringComparison.Ordinal)).Select(a => a.RoleCode).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The hashing scheme. Stored with every version hash (<c>v1:</c> + hex), so a release that changes the canonical text
    /// must ship a reviewed migration that re-hashes, instead of locking every deployment out (DL-061c).
    /// </summary>
    public const string HashScheme = "v1";

    /// <summary>
    /// SHA-256 (lower-case hex) of the whole model in a fixed order, without its version, under <see cref="HashScheme"/>.
    /// Stored with every version, so a read can tell whether the current tables are still the model that version produced.
    /// Not keyed: it detects edits that don't also forge the version history (R-033); a keyed hash waits for the vault.
    /// </summary>
    public string ContentHash()
    {
        var text = new System.Text.StringBuilder();
        text.Append("iam-access-model/").Append(HashScheme).Append((char)0x0A);
        static void Field(System.Text.StringBuilder sb, string value) =>
            sb.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        foreach (var role in Roles)
        {
            text.Append('R');
            Field(text, role.Code);
            Field(text, role.Name);
            Field(text, role.Assignable ? "1" : "0");
            foreach (var action in role.Actions.Order(StringComparer.Ordinal))
            {
                Field(text, action);
            }

            text.Append((char)0x0A);
        }

        foreach (var assignment in Assignments)
        {
            text.Append('A');
            Field(text, assignment.Subject);
            Field(text, assignment.RoleCode);
            text.Append((char)0x0A);
        }

        foreach (var pair in IncompatiblePairs)
        {
            text.Append('P');
            Field(text, pair.First);
            Field(text, pair.Second);
            text.Append((char)0x0A);
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Checks a change against this model without applying it.</summary>
    public void Check(AccessChange change) => _ = Apply(change);

    /// <summary>
    /// Applies a change and returns the new model, or refuses it. After any change someone must still be able to propose and
    /// a different person able to approve, or no further change could ever be made.
    /// </summary>
    internal AccessModel Apply(AccessChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = Next();
        next.Add(change);
        next.RequireLimits();
        next.RequireSeparatedDuties();
        next.RequireNoLockout();
        return next;
    }

    private static Role Copy(Role role) => role with { Actions = Array.AsReadOnly(role.Actions.ToArray()) };

    private AccessModel Next() =>
        new(new Dictionary<string, Role>(_roles, StringComparer.Ordinal), [.. _assignments], [.. _pairs], Version + 1);

    private void Add(AccessChange change)
    {
        switch (change)
        {
            case AssignRole assign:
                ValidateAssignment(assign.Subject, assign.RoleCode);
                if (!_assignments.Add(new Assignment(assign.Subject, assign.RoleCode)))
                {
                    throw new AccessRefusedException(AccessRefusal.AlreadyAssigned);
                }

                break;
            case RemoveAssignment remove:
                if (remove.Subject is null || remove.RoleCode is null || !_assignments.Remove(new Assignment(remove.Subject, remove.RoleCode)))
                {
                    throw new AccessRefusedException(AccessRefusal.NotAssigned);
                }

                break;
            case DefineRole define:
                var role = new Role(define.Code, define.Name, define.Actions ?? []);
                ValidateRole(role);
                if (_roles.TryGetValue(role.Code, out var existing) && !existing.Assignable)
                {
                    throw new AccessRefusedException(AccessRefusal.ReservedRole);
                }

                _roles[role.Code] = Copy(role);
                break;
            case AddIncompatiblePair add:
                ValidatePair(add.First, add.Second);
                var pair = IncompatiblePair.Of(add.First, add.Second);
                if (_assignments.GroupBy(a => a.Subject, StringComparer.Ordinal).Any(g => g.Any(a => a.RoleCode == pair.First) && g.Any(a => a.RoleCode == pair.Second)))
                {
                    throw new AccessRefusedException(AccessRefusal.PairConflictsWithAssignments);
                }

                if (!_pairs.Add(pair))
                {
                    throw new AccessRefusedException(AccessRefusal.PairAlreadyExists);
                }

                break;
            case RemoveIncompatiblePair removePair:
                if (removePair.First is null || removePair.Second is null || !_pairs.Remove(IncompatiblePair.Of(removePair.First, removePair.Second)))
                {
                    throw new AccessRefusedException(AccessRefusal.UnknownPair);
                }

                break;
            default:
                throw new AccessRefusedException(AccessRefusal.InvalidChange);
        }
    }

    private void ValidateAssignment(string? subject, string? roleCode)
    {
        RequireSubject(subject);
        if (roleCode is null || !_roles.TryGetValue(roleCode, out var role))
        {
            throw new AccessRefusedException(AccessRefusal.UnknownRole);
        }

        if (!role.Assignable)
        {
            throw new AccessRefusedException(AccessRefusal.RoleNotAssignable);
        }

        // Checked against the roles the person already holds, so two separate changes can't build a forbidden pair.
        foreach (var held in _assignments.Where(a => string.Equals(a.Subject, subject, StringComparison.Ordinal)))
        {
            if (_pairs.Contains(IncompatiblePair.Of(held.RoleCode, roleCode)))
            {
                throw new AccessRefusedException(AccessRefusal.IncompatibleRoles);
            }
        }
    }

    private void ValidatePair(string? first, string? second)
    {
        if (first is null || second is null || !_roles.ContainsKey(first) || !_roles.ContainsKey(second))
        {
            throw new AccessRefusedException(AccessRefusal.UnknownRole);
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new AccessRefusedException(AccessRefusal.SameRoleTwice);
        }
    }

    private static void ValidateRole(Role? role)
    {
        if (role is null || !IsRoleCode(role.Code))
        {
            throw new AccessRefusedException(AccessRefusal.InvalidRoleCode);
        }

        if (!AccessText.IsName(role.Name, AccessLimits.MaxRoleNameLength))
        {
            throw new AccessRefusedException(AccessRefusal.InvalidRoleName);
        }

        if (role.Actions is null || role.Actions.Count > ActionCatalog.All.Count)
        {
            throw new AccessRefusedException(AccessRefusal.TooManyActions);
        }

        foreach (var action in role.Actions)
        {
            if (!ActionCatalog.IsKnown(action))
            {
                throw new AccessRefusedException(AccessRefusal.UnknownAction);
            }

            if (ActionCatalog.IsEveryone(action))
            {
                throw new AccessRefusedException(AccessRefusal.ActionNotGrantable);
            }
        }

        if (role.Actions.Distinct(StringComparer.Ordinal).Count() != role.Actions.Count)
        {
            throw new AccessRefusedException(AccessRefusal.DuplicateAction);
        }

        if (HoldsBothDuties(role.Actions))
        {
            throw new AccessRefusedException(AccessRefusal.SeparatedDuties);
        }
    }

    /// <summary>Lower-case letters, digits and single hyphens, starting with a letter: a code, never free text.</summary>
    private static bool IsRoleCode(string? code) =>
        code is { Length: > 0 and <= AccessLimits.MaxRoleCodeLength }
        && code[0] is >= 'a' and <= 'z'
        && code[^1] != '-'
        && !code.Contains("--", StringComparison.Ordinal)
        && code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static void RequireSubject(string? subject)
    {
        if (!AccessText.IsSubject(subject))
        {
            throw new AccessRefusedException(AccessRefusal.InvalidSubject);
        }
    }

    private void RequireLimits()
    {
        if (_roles.Count > AccessLimits.MaxRoles)
        {
            throw new AccessRefusedException(AccessRefusal.TooManyRoles);
        }

        if (_assignments.Count > AccessLimits.MaxAssignments)
        {
            throw new AccessRefusedException(AccessRefusal.TooManyAssignments);
        }
    }

    private static bool HoldsBothDuties(IEnumerable<string> actions)
    {
        var set = actions as IReadOnlySet<string> ?? actions.ToHashSet(StringComparer.Ordinal);
        return ActionCatalog.ConflictingActions.Any(pair => set.Contains(pair.First) && set.Contains(pair.Second));
    }

    /// <summary>
    /// Conflicting duties (<see cref="ActionCatalog.ConflictingActions"/>) are checked on actions rather than role names, so
    /// no new role or combination of roles can give one person both.
    /// </summary>
    private void RequireSeparatedDuties()
    {
        foreach (var subject in _assignments.Select(a => a.Subject).Distinct(StringComparer.Ordinal))
        {
            if (HoldsBothDuties(ActionsOf(subject)))
            {
                throw new AccessRefusedException(AccessRefusal.SeparatedDuties);
            }
        }
    }

    private void RequireNoLockout()
    {
        var subjects = _assignments.Select(a => a.Subject).Distinct(StringComparer.Ordinal).ToList();
        var proposers = subjects.Where(s => Can(s, ActionCatalog.ChangesPropose)).ToList();
        var approvers = subjects.Where(s => Can(s, ActionCatalog.ChangesApprove)).ToList();
        if (!proposers.Exists(p => approvers.Exists(a => !string.Equals(a, p, StringComparison.Ordinal))))
        {
            throw new AccessRefusedException(AccessRefusal.WouldLockOut);
        }
    }
}

/// <summary>Text rules: the shared second-person rules (one definition everywhere), bounded by access-control limits.</summary>
internal static class AccessText
{
    /// <summary>A subject is an opaque id from the identity provider: printable, no surrounding spaces, bounded.</summary>
    public static bool IsSubject(string? subject) => subject is { Length: <= AccessLimits.MaxSubjectLength } && SecondPersonText.IsPerson(subject);

    public static bool IsName(string? name, int maxLength) => SecondPersonText.IsText(name, maxLength);

    public static bool IsReason(string? reason) => SecondPersonText.IsText(reason, AccessLimits.MaxReasonLength);
}
