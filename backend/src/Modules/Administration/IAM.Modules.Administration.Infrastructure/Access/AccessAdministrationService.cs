using System.Diagnostics;
using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Domain.Access;
using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Administration.Infrastructure.Access;

/// <summary>Audit action codes of access control (registered with the audit writer by the Administration module).</summary>
public static class AccessAuditActions
{
    public const string Bootstrapped = "administration.access.bootstrapped";
    public const string IntegrityFailed = "administration.access.integrity_failed";
    public const string Proposed = "administration.access_change.proposed";
    public const string Approved = "administration.access_change.approved";
    public const string Rejected = "administration.access_change.rejected";
    public const string Withdrawn = "administration.access_change.withdrawn";
    public const string Refused = "administration.access_change.refused";
    public const string ModelTargetType = "administration.access_model";
    public const string ChangeTargetType = "administration.access_change_request";

    public static IReadOnlyList<string> All { get; } = [Bootstrapped, IntegrityFailed, Proposed, Approved, Rejected, Withdrawn, Refused];
}

/// <summary>
/// Stored access control. Every change and its audit event commit together, or neither does (ADR-0018). A refusal is
/// evidence too: it is audited as denied, then refused, and nothing else changes. Stored data that breaks the rules or
/// no longer matches its version's hash is audited as a failure and raised as <see cref="AccessStoreIntegrityException"/>.
/// <para>
/// Applying a change first claims the next model version (inserted and saved before anything else is written). Only one
/// transaction can claim a number, so of two concurrent approvals the second fails at once with a conflict, and the one
/// that continues knows the tables still hold exactly the model it read.
/// </para>
/// Not-found requests and conflicts are not audited: nothing was decided and nothing changed.
/// </summary>
public sealed class AccessAdministrationService(
    AdministrationDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor) : IAccessAdministration
{
    private const int ReadAttempts = 3;

    public Task<AccessModel> CurrentModelAsync(CancellationToken cancellationToken) =>
        GuardAsync("read", null, () => ReadModelAsync(cancellationToken), cancellationToken);

    public Task<ChangeRequestView?> FindChangeAsync(Guid id, CancellationToken cancellationToken) =>
        GuardAsync("read", id, async () =>
        {
            var row = await db.ChangeRequests.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
            return row is null ? null : View(Restore(row));
        }, cancellationToken);

    public Task<ChangeRequestView> ProposeAsync(Guid id, AccessChange change, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return GuardAsync("propose", id, async () =>
        {
            var actor = Actor();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (await db.ChangeRequests.AnyAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false))
            {
                // Someone else's request already has this id; a refusal audit must not be filed against it.
                throw new AccessConflictException();
            }

            var model = await ReadModelAsync(cancellationToken).ConfigureAwait(false);
            ChangeRequest proposed;
            try
            {
                proposed = ChangeRequest.Propose(id, change, actor, reason, model, Now());
            }
            catch (AccessRefusedException refusal)
            {
                await RefuseAsync(tx, id, "propose", refusal, model, cancellationToken).ConfigureAwait(false);
                throw;
            }

            var (kind, payload) = AccessChangeCodec.Encode(proposed.Change);
            db.ChangeRequests.Add(new AccessChangeRequestRow
            {
                Id = proposed.Id,
                Kind = kind,
                Payload = payload,
                Maker = proposed.Maker,
                Reason = proposed.Reason,
                ProposedAtUtc = proposed.ProposedAtUtc,
                ProposedAgainstVersion = proposed.ProposedAgainstVersion,
                State = proposed.State.ToString(),
            });
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), Record(AccessAuditActions.Proposed, proposed, proposed.Reason, model, null), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return View(proposed);
        }, cancellationToken);
    }

    public Task<ChangeRequestView> ApproveAsync(Guid id, string? reason, CancellationToken cancellationToken) =>
        GuardAsync("approve", id, async () =>
        {
            var actor = Actor();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // The model first, then the request: a decision committed in between shows up as a request that is no longer
            // proposed, never as a stale request checked against a newer model.
            var before = await ReadModelAsync(cancellationToken).ConfigureAwait(false);
            var row = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            var change = Restore(row);
            AccessModel after;
            try
            {
                after = change.Approve(actor, reason, before, Now());
            }
            catch (AccessRefusedException refusal)
            {
                await RefuseAsync(tx, id, "approve", refusal, before, cancellationToken).ConfigureAwait(false);
                throw;
            }

            await ClaimVersionAsync(after, AccessModelVersionRow.KindChange, change.Id, actor, change.DecidedAtUtc!.Value, cancellationToken).ConfigureAwait(false);
            await WriteModelChangesAsync(before, after, cancellationToken).ConfigureAwait(false);
            Apply(row, change);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), Record(AccessAuditActions.Approved, change, change.DecisionReason, before, after), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return View(change);
        }, cancellationToken);

    public Task<ChangeRequestView> RejectAsync(Guid id, string reason, CancellationToken cancellationToken) =>
        GuardAsync("reject", id, async () =>
        {
            var actor = Actor();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var model = await ReadModelAsync(cancellationToken).ConfigureAwait(false);
            var row = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            var change = Restore(row);
            try
            {
                change.Reject(actor, reason, model, Now());
            }
            catch (AccessRefusedException refusal)
            {
                await RefuseAsync(tx, id, "reject", refusal, model, cancellationToken).ConfigureAwait(false);
                throw;
            }

            Apply(row, change);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), Record(AccessAuditActions.Rejected, change, change.DecisionReason, model, null), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return View(change);
        }, cancellationToken);

    public Task<ChangeRequestView> WithdrawAsync(Guid id, string? reason, CancellationToken cancellationToken) =>
        GuardAsync("withdraw", id, async () =>
        {
            var actor = Actor();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var model = await ReadModelAsync(cancellationToken).ConfigureAwait(false);
            var row = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            var change = Restore(row);
            try
            {
                change.Withdraw(actor, reason, Now());
            }
            catch (AccessRefusedException refusal)
            {
                await RefuseAsync(tx, id, "withdraw", refusal, model, cancellationToken).ConfigureAwait(false);
                throw;
            }

            Apply(row, change);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), Record(AccessAuditActions.Withdrawn, change, change.DecisionReason, model, null), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return View(change);
        }, cancellationToken);

    public Task BootstrapAsync(string administrator, string manager, CancellationToken cancellationToken) =>
        GuardAsync("bootstrap", null, async () =>
        {
            var actor = Actor();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadModelAsync(cancellationToken).ConfigureAwait(false);
            AccessModel model;
            try
            {
                if (current.Version > 0 || current.Roles.Count > 0)
                {
                    throw new AccessRefusedException(AccessRefusal.AlreadyBootstrapped);
                }

                model = AccessModel.Defaults().Bootstrap(administrator, manager);
            }
            catch (AccessRefusedException refusal)
            {
                await RefuseAsync(tx, null, "bootstrap", refusal, current, cancellationToken).ConfigureAwait(false);
                throw;
            }

            var now = Now();
            await ClaimVersionAsync(model, AccessModelVersionRow.KindBootstrap, null, actor, now, cancellationToken).ConfigureAwait(false);
            await WriteModelChangesAsync(current, model, cancellationToken).ConfigureAwait(false);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            var record = new AuditEventRecord
            {
                Action = AccessAuditActions.Bootstrapped,
                TargetType = AccessAuditActions.ModelTargetType,
                TargetId = model.Version.ToString(CultureInfo.InvariantCulture),
                Outcome = AuditOutcome.Succeeded,
                OccurredAtUtc = now,
                CorrelationId = CorrelationId(),
                PolicyVersionRef = VersionRef(model),
                PolicyVersionHash = model.ContentHash(),
                Payload =
                [
                    new AuditPayloadField("administrator", administrator, AuditFieldClass.Plain),
                    new AuditPayloadField("manager", manager, AuditFieldClass.Plain),
                ],
            };
            await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Reads the tables between two reads of the latest version. Every stored change adds a version in the same
    /// transaction, so the same version before and after means no change committed in between. The model must then pass
    /// every rule and match the hash stored with that version; otherwise the store is damaged.
    /// </summary>
    private async Task<AccessModel> ReadModelAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            var before = await LatestVersionAsync(cancellationToken).ConfigureAwait(false);
            var roles = await db.Roles.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var actions = await db.RoleActions.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var assignments = await db.Assignments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var pairs = await db.IncompatiblePairs.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var after = await LatestVersionAsync(cancellationToken).ConfigureAwait(false);
            if (before != after)
            {
                continue;
            }

            AccessModel model;
            try
            {
                model = AccessModel.Load(
                    roles.Select(r => new Role(r.Code, r.Name, [.. actions.Where(a => a.RoleCode == r.Code).Select(a => a.ActionId).Order(StringComparer.Ordinal)], r.Assignable)),
                    assignments.Select(a => new Assignment(a.Subject, a.RoleCode)),
                    pairs.Select(p => new IncompatiblePair(p.First, p.Second)),
                    after?.ModelVersion ?? 0);
            }
            catch (AccessRefusedException refusal)
            {
                throw new AccessStoreIntegrityException("rule", $"The stored access model breaks a rule ({refusal.Refusal}).", refusal);
            }

            var expected = after?.ModelHash ?? StoredHash(AccessModel.Load([], [], [], 0));
            if (!string.Equals(StoredHash(model), expected, StringComparison.Ordinal))
            {
                throw new AccessStoreIntegrityException("hash", "The stored access model does not match the hash of its latest version.");
            }

            // The latest version must be the bootstrap, or name a request that was approved as exactly this version. A version
            // claimed but never completed, or pointing at a rejected request, means the history was written outside the service.
            if (after is { ChangeRequestId: { } requestId } latest)
            {
                var appliedAs = await db.ChangeRequests.AsNoTracking()
                    .Where(c => c.Id == requestId && c.State == nameof(ChangeState.Approved))
                    .Select(c => c.AppliedAsVersion)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (appliedAs != latest.ModelVersion)
                {
                    throw new AccessStoreIntegrityException("link", "The latest access model version does not name the request approved as it.");
                }
            }

            return model;
        }

        throw new AccessConflictException();
    }

    private Task<LatestVersion?> LatestVersionAsync(CancellationToken cancellationToken) =>
        db.ModelVersions.AsNoTracking()
            .OrderByDescending(v => v.ModelVersion)
            .Select(v => new LatestVersion(v.ModelVersion, v.ModelHash, v.ChangeRequestId))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Inserts and saves the version row first. A second claimant of the same number fails here with a conflict.</summary>
    private async Task ClaimVersionAsync(AccessModel model, string kind, Guid? changeRequestId, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        db.ModelVersions.Add(new AccessModelVersionRow
        {
            ModelVersion = model.Version,
            Kind = kind,
            ChangeRequestId = changeRequestId,
            ModelHash = StoredHash(model),
            AppliedBy = actor,
            AppliedAtUtc = at,
        });
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (changeRequestId is { } id)
        {
            // The database refuses a claim for a request that is no longer proposed: a reject or withdraw (or another
            // approval) committed after this approval read the request. That is a conflict, not a fault. Nothing of this
            // operation is kept, so roll back (on SQL Server the trigger's THROW may already have) and read the state afresh.
            string? state;
            try
            {
                db.ChangeTracker.Clear();
                if (db.Database.CurrentTransaction is { } current)
                {
                    await current.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }

                state = await db.ChangeRequests.AsNoTracking().Where(c => c.Id == id).Select(c => c.State).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception lookup) when (lookup is not OperationCanceledException)
            {
                throw new InvalidOperationException("Claiming the next access model version failed.", new AggregateException(ex, lookup));
            }

            if (state != nameof(ChangeState.Proposed))
            {
                throw new AccessConflictException("The access change request was decided by someone else; reload it.", ex);
            }

            // A neutral message: the database error can name people and role codes (R-033).
            throw new InvalidOperationException("Claiming the next access model version failed.", ex);
        }
    }

    private static ChangeRequest Restore(AccessChangeRequestRow row)
    {
        try
        {
            return ChangeRequest.Restore(
                row.Id,
                AccessChangeCodec.Decode(row.Kind, row.Payload),
                row.Maker,
                row.Reason,
                row.ProposedAtUtc,
                row.ProposedAgainstVersion,
                ParseState(row.State),
                row.Checker,
                row.DecisionReason,
                row.DecidedAtUtc,
                row.AppliedAsVersion);
        }
        catch (AccessRefusedException refusal)
        {
            throw new AccessStoreIntegrityException("change", $"A stored access change request is not valid ({refusal.Refusal}).", refusal);
        }
    }

    /// <summary>Exact names only: no numbers, flags or other spellings.</summary>
    private static ChangeState ParseState(string state) => state switch
    {
        nameof(ChangeState.Proposed) => ChangeState.Proposed,
        nameof(ChangeState.Approved) => ChangeState.Approved,
        nameof(ChangeState.Rejected) => ChangeState.Rejected,
        nameof(ChangeState.Withdrawn) => ChangeState.Withdrawn,
        _ => 0,
    };

    private static void Apply(AccessChangeRequestRow row, ChangeRequest change)
    {
        row.State = change.State.ToString();
        row.Checker = change.Checker;
        row.DecisionReason = change.DecisionReason;
        row.DecidedAtUtc = change.DecidedAtUtc;
        row.AppliedAsVersion = change.AppliedAsVersion;
    }

    private static ChangeRequestView View(ChangeRequest c) => new(
        c.Id, c.Change, c.Maker, c.Reason, c.ProposedAtUtc, c.ProposedAgainstVersion, c.State, c.Checker, c.DecisionReason, c.DecidedAtUtc, c.AppliedAsVersion);

    private static string CorrelationId() => Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");

    private static string StoredHash(AccessModel model) => AccessModel.HashScheme + ":" + model.ContentHash();

    private static string VersionRef(AccessModel model) => "access-model:" + model.Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>The clock at storage precision, so what a call returns is exactly what a later read gives back.</summary>
    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    /// <summary>The server-side actor, held to the same rules as any assigned person, so every actor can be stored and audited.</summary>
    private string Actor() =>
        actorAccessor.CurrentActor is { } actor && actor.Length is > 0 and <= AccessLimits.MaxSubjectLength && actor.Trim().Length == actor.Length
            ? actor
            : throw new AccessRefusedException(AccessRefusal.NotAllowed);

    private async Task<AccessChangeRequestRow> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.ChangeRequests.SingleOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false)
        ?? throw new AccessChangeNotFoundException();

    /// <summary>
    /// Writes only what differs between the two models, so the tables equal the model after the change. Runs after the
    /// version is claimed, when no other change can have been committed since <paramref name="before"/> was read.
    /// </summary>
    private async Task WriteModelChangesAsync(AccessModel before, AccessModel after, CancellationToken cancellationToken)
    {
        var oldRoles = before.Roles.ToDictionary(r => r.Code, StringComparer.Ordinal);
        foreach (var role in after.Roles)
        {
            if (!oldRoles.TryGetValue(role.Code, out var old))
            {
                db.Roles.Add(new AccessRoleRow { Code = role.Code, Name = role.Name, Assignable = role.Assignable });
                db.RoleActions.AddRange(role.Actions.Select(a => new AccessRoleActionRow { RoleCode = role.Code, ActionId = a }));
                continue;
            }

            if (!string.Equals(old.Name, role.Name, StringComparison.Ordinal) || old.Assignable != role.Assignable)
            {
                var stored = await db.Roles.SingleAsync(r => r.Code == role.Code, cancellationToken).ConfigureAwait(false);
                stored.Name = role.Name;
                stored.Assignable = role.Assignable;
            }

            var added = role.Actions.Except(old.Actions, StringComparer.Ordinal).ToList();
            var removed = old.Actions.Except(role.Actions, StringComparer.Ordinal).ToList();
            db.RoleActions.AddRange(added.Select(a => new AccessRoleActionRow { RoleCode = role.Code, ActionId = a }));
            if (removed.Count > 0)
            {
                var gone = await db.RoleActions.Where(a => a.RoleCode == role.Code && removed.Contains(a.ActionId)).ToListAsync(cancellationToken).ConfigureAwait(false);
                db.RoleActions.RemoveRange(gone);
            }
        }

        var oldAssignments = before.Assignments.ToHashSet();
        var newAssignments = after.Assignments.ToHashSet();
        db.Assignments.AddRange(newAssignments.Except(oldAssignments).Select(a => new AccessRoleAssignmentRow { Subject = a.Subject, RoleCode = a.RoleCode }));
        foreach (var gone in oldAssignments.Except(newAssignments))
        {
            var row = await db.Assignments.SingleAsync(a => a.Subject == gone.Subject && a.RoleCode == gone.RoleCode, cancellationToken).ConfigureAwait(false);
            db.Assignments.Remove(row);
        }

        var oldPairs = before.IncompatiblePairs.ToHashSet();
        var newPairs = after.IncompatiblePairs.ToHashSet();
        db.IncompatiblePairs.AddRange(newPairs.Except(oldPairs).Select(p => new AccessIncompatibleRolePairRow { First = p.First, Second = p.Second }));
        foreach (var gone in oldPairs.Except(newPairs))
        {
            var row = await db.IncompatiblePairs.SingleAsync(p => p.First == gone.First && p.Second == gone.Second, cancellationToken).ConfigureAwait(false);
            db.IncompatiblePairs.Remove(row);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex) || databaseProvider.IsConcurrencyFailure(ex))
        {
            // Another decision on the same request, another approval claiming the same model version, the database picking
            // this transaction as a deadlock loser, or the same id proposed twice. Nothing was saved. The inner exception
            // can hold key values (people, role codes): callers must not return or log it unredacted.
            throw new AccessConflictException("The access model or change request was changed by someone else; reload it and try again.", ex);
        }
    }

    /// <summary>
    /// Runs one operation. A damaged store is audited as a failure in its own transaction (the operation's own was rolled
    /// back), then reported. A deadlock or serialization failure outside a save is reported as a conflict.
    /// </summary>
    private async Task<T> GuardAsync<T>(string step, Guid? id, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (AccessStoreIntegrityException integrity)
        {
            try
            {
                await RecordIntegrityFailureAsync(step, id, integrity.Check, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception auditFailure)
            {
                throw new AccessStoreIntegrityException(integrity.Check ?? "unknown", integrity.Message, integrity) { AuditFailure = auditFailure };
            }

            throw;
        }
        catch (Exception ex) when (ex is not AccessConflictException && databaseProvider.IsConcurrencyFailure(ex))
        {
            throw new AccessConflictException("The access model or change request was changed by someone else; reload it and try again.", ex);
        }
    }

    /// <summary>
    /// Written with no caller cancellation, so a client that disconnects can't suppress it. Repeated failures are each
    /// recorded; slice 3 must rate-limit before every request reads the model (R-033).
    /// </summary>
    private async Task RecordIntegrityFailureAsync(string step, Guid? id, string? check, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = new AuditEventRecord
        {
            Action = AccessAuditActions.IntegrityFailed,
            TargetType = id is null ? AccessAuditActions.ModelTargetType : AccessAuditActions.ChangeTargetType,
            TargetId = id?.ToString(),
            Outcome = AuditOutcome.Failed,
            OccurredAtUtc = Now(),
            CorrelationId = CorrelationId(),
            Payload =
            [
                new AuditPayloadField("step", step, AuditFieldClass.Plain),
                new AuditPayloadField("check", check ?? "unknown", AuditFieldClass.Plain),
            ],
        };
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The refusal is evidence (rule 9): it is audited as denied, with only the refusal code, then committed.</summary>
    private async Task RefuseAsync(IDbContextTransaction tx, Guid? id, string step, AccessRefusedException refusal, AccessModel model, CancellationToken cancellationToken)
    {
        // Nothing has been saved before any refusal, so clearing the tracker leaves only the audit row to commit.
        db.ChangeTracker.Clear();
        var record = new AuditEventRecord
        {
            Action = AccessAuditActions.Refused,
            TargetType = id is null ? AccessAuditActions.ModelTargetType : AccessAuditActions.ChangeTargetType,
            TargetId = id?.ToString(),
            Outcome = AuditOutcome.Denied,
            OccurredAtUtc = Now(),
            CorrelationId = CorrelationId(),
            PolicyVersionRef = VersionRef(model),
            PolicyVersionHash = model.ContentHash(),
            Payload =
            [
                new AuditPayloadField("step", step, AuditFieldClass.Plain),
                new AuditPayloadField("refusal", (refusal.Refusal ?? AccessRefusal.InvalidChange).ToString(), AuditFieldClass.Plain),
            ],
        };
        await audit.AppendAsync(tx.GetDbTransaction(), record, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The decision's evidence: what changed, against which model version and hash, and for an approval the version and
    /// hash it produced. A role definition also records the role as it was before.
    /// </summary>
    private AuditEventRecord Record(string action, ChangeRequest change, string? reason, AccessModel against, AccessModel? produced)
    {
        var (kind, _) = AccessChangeCodec.Encode(change.Change);
        var payload = new List<AuditPayloadField>
        {
            new("kind", kind, AuditFieldClass.Plain),
            new("state", change.State.ToString(), AuditFieldClass.Plain),
            new("maker", change.Maker, AuditFieldClass.Plain),
        };
        switch (change.Change)
        {
            case AssignRole a:
                payload.Add(new("subject", a.Subject, AuditFieldClass.Plain));
                payload.Add(new("roleCode", a.RoleCode, AuditFieldClass.Plain));
                break;
            case RemoveAssignment r:
                payload.Add(new("subject", r.Subject, AuditFieldClass.Plain));
                payload.Add(new("roleCode", r.RoleCode, AuditFieldClass.Plain));
                break;
            case DefineRole d:
                payload.Add(new("roleCode", d.Code, AuditFieldClass.Plain));
                payload.Add(new("roleName", d.Name, AuditFieldClass.Plain));
                payload.Add(new("actions", string.Join(',', d.Actions), AuditFieldClass.Plain));
                if (against.Roles.FirstOrDefault(r => r.Code == d.Code) is { } previous)
                {
                    payload.Add(new("previousRoleName", previous.Name, AuditFieldClass.Plain));
                    payload.Add(new("previousActions", string.Join(',', previous.Actions), AuditFieldClass.Plain));
                }

                break;
            case AddIncompatiblePair p:
                payload.Add(new("first", p.First, AuditFieldClass.Plain));
                payload.Add(new("second", p.Second, AuditFieldClass.Plain));
                break;
            case RemoveIncompatiblePair q:
                payload.Add(new("first", q.First, AuditFieldClass.Plain));
                payload.Add(new("second", q.Second, AuditFieldClass.Plain));
                break;
        }

        if (produced is not null)
        {
            payload.Add(new("modelVersion", produced.Version.ToString(CultureInfo.InvariantCulture), AuditFieldClass.Plain));
            payload.Add(new("modelHash", produced.ContentHash(), AuditFieldClass.Plain));
        }

        return new AuditEventRecord
        {
            Action = action,
            TargetType = AccessAuditActions.ChangeTargetType,
            TargetId = change.Id.ToString(),
            Outcome = AuditOutcome.Succeeded,
            OccurredAtUtc = Now(),
            CorrelationId = CorrelationId(),
            Reason = reason,
            PolicyVersionRef = VersionRef(against),
            PolicyVersionHash = against.ContentHash(),
            Payload = payload,
        };
    }

    private sealed record LatestVersion(long ModelVersion, string ModelHash, Guid? ChangeRequestId);
}
