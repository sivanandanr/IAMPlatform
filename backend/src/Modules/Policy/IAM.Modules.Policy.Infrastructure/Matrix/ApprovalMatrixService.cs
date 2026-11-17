using System.Diagnostics;
using System.Globalization;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Identity.Contracts;
using IAM.Modules.Policy.Application.Matrix;
using IAM.Modules.Policy.Application.Rules;
using IAM.Modules.Policy.Contracts;
using IAM.Modules.Policy.Domain.Matrix;
using IAM.Modules.Policy.Domain.Rules;
using IAM.Modules.Policy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Policy.Infrastructure.Matrix;

/// <summary>Audit action codes of the approval matrix (registered with the audit writer by the Policy module).</summary>
public static class MatrixAuditActions
{
    public const string DraftSet = "policy.matrix.draft_set";
    public const string Proposed = "policy.matrix.proposed";
    public const string Approved = "policy.matrix.approved";
    public const string Rejected = "policy.matrix.rejected";
    public const string Withdrawn = "policy.matrix.withdrawn";
    public const string Refused = "policy.matrix.refused";
    public const string Target = "policy.approval_matrix";

    public const string ReadAction = AccessActions.MatrixRead;
    public const string ManageAction = AccessActions.MatrixManage;
    public const string ApproveAction = AccessActions.MatrixApprove;

    public static IReadOnlyList<string> All { get; } = [DraftSet, Proposed, Approved, Rejected, Withdrawn, Refused];
}

/// <summary>
/// Compiles matrix documents against the catalog: the built-in facts plus the published attributes the conditions name,
/// read through Identity's contract. An attribute that is neither is refused.
/// </summary>
public sealed class MatrixCompiler(IAttributeCatalog catalog)
{
    /// <summary>
    /// The version id compiled into the stored hash. The rules engine's hash covers the version id; the stored compiled hash
    /// must describe content only (the content hash and version row carry the version), so it is always computed with this.
    /// </summary>
    public static readonly Guid HashVersion = Guid.Empty;

    public async Task<CompiledMatrix> CompileAsync(Guid versionId, MatrixDocument document, CancellationToken cancellationToken)
    {
        var specs = BuiltInFacts.All.ToDictionary(f => f.Spec.AttributeId, f => f.Spec);
        foreach (var id in MatrixDocuments.AttributeIds(document))
        {
            if (specs.ContainsKey(id))
            {
                continue;
            }

            var published = await catalog.FindPublishedByIdAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new MatrixRefusedException(MatrixRefusal.UnknownAttribute);
            try
            {
                specs[id] = AttributeSpecMapper.Map(published);
            }
            catch (RuleRejectedException ex)
            {
                throw new MatrixRefusedException(MatrixRefusal.UnknownAttribute, ex);
            }
        }

        return MatrixDocuments.Compile(versionId, document, specs);
    }
}

/// <summary>
/// Approval matrix administration (DL-090). Each change and its audit event are saved in one transaction (ADR-0018); every
/// refused write, including a missing permission, is audited as denied with its code.
/// </summary>
public sealed class ApprovalMatrixService(
    PolicyDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions,
    MatrixCompiler compiler) : IApprovalMatrixAdministration
{
    private const string ProposedState = nameof(SecondPersonState.Proposed);

    public async Task<ApprovalMatrixView?> FindAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(MatrixAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var matrix = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return matrix is null ? null : await ViewAsync(matrix, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApprovalMatrixView> SetDraftAsync(long? expectedConcurrencyVersion, string document, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var matrix = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            await RequireAsync(MatrixAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            var parsed = MatrixDocuments.Parse(document);
            var compiled = await compiler.CompileAsync(MatrixCompiler.HashVersion, parsed, cancellationToken).ConfigureAwait(false);
            if (matrix is null)
            {
                matrix = ApprovalMatrix.Create(parsed, compiled.Policy.ContentHash, actor);
                db.Matrices.Add(matrix);
                changed = true;
            }
            else
            {
                if (db.Entry(matrix).Property<long>(AuditColumns.Version).CurrentValue != expectedConcurrencyVersion)
                {
                    throw new MatrixConflictException();
                }

                await RequireNothingPendingAsync(matrix.Id, cancellationToken).ConfigureAwait(false);
                var highest = await db.MatrixVersions.Where(v => v.MatrixId == matrix.Id).MaxAsync(v => v.VersionNumber, cancellationToken).ConfigureAwait(false);
                changed = matrix.SetDraft(parsed, compiled.Policy.ContentHash, highest, actor);
                Touch(matrix);
            }
        }
        catch (MatrixRefusedException refusal)
        {
            await RefuseAsync(tx, matrix?.Id, "set_draft", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(MatrixAuditActions.DraftSet, matrix, matrix.CurrentDraft!, null, null), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ViewAsync(matrix, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MatrixChangeRequestView> ProposeAsync(string reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var matrix = await LoadAsync(cancellationToken).ConfigureAwait(false) ?? throw new MatrixNotFoundException();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        MatrixChangeRequest proposed;
        try
        {
            await RequireAsync(MatrixAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            await RequireNothingPendingAsync(matrix.Id, cancellationToken).ConfigureAwait(false);
            var draft = matrix.CurrentDraft ?? throw new MatrixRefusedException(MatrixRefusal.NoDraft);
            var compiled = await compiler.CompileAsync(MatrixCompiler.HashVersion, draft.ToDocument(), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(compiled.Policy.ContentHash, draft.CompiledHash, StringComparison.Ordinal))
            {
                throw new MatrixRefusedException(MatrixRefusal.AttributesChanged);
            }

            proposed = MatrixChangeRequest.Propose(Guid.CreateVersion7(), matrix, compiled.Publishable, actor, reason, Now());
        }
        catch (MatrixRefusedException refusal)
        {
            await RefuseAsync(tx, matrix.Id, "propose", refusal, null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        db.MatrixRequests.Add(new MatrixChangeRequestRow
        {
            Id = proposed.Id,
            MatrixId = proposed.MatrixId,
            VersionId = proposed.VersionId,
            VersionNumber = proposed.VersionNumber,
            ContentHash = proposed.ContentHash,
            CompiledHash = proposed.CompiledHash,
            Maker = proposed.Maker,
            Reason = proposed.Reason,
            ProposedAtUtc = proposed.ProposedAtUtc,
            State = proposed.State.ToString(),
        });
        Touch(matrix);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(MatrixAuditActions.Proposed, matrix, matrix.CurrentDraft!, proposed, proposed.Reason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(proposed);
    }

    public async Task<MatrixChangeReview?> ReviewAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await RequireAsync(MatrixAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        var row = await db.MatrixRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var matrix = await LoadAsync(cancellationToken).ConfigureAwait(false) ?? throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        var draft = matrix.CurrentDraft;
        var named = draft is not null && draft.Id == row.VersionId && string.Equals(draft.ContentHash, row.ContentHash, StringComparison.Ordinal);
        return new MatrixChangeReview(View(Restore(row)), named ? await ViewAsync(draft!, cancellationToken).ConfigureAwait(false) : null);
    }

    public async Task<MatrixChangeRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var matrix = await LoadAsync(cancellationToken).ConfigureAwait(false) ?? throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        MatrixChangeRequest request;
        MatrixVersion draft;
        try
        {
            request = Restore(row);
            request.RequireDecider(actor);
            await RequireAsync(MatrixAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            await RequirePersonAsync(request.Maker, MatrixAuditActions.ManageAction, MatrixRefusal.MakerNoLongerAllowed, cancellationToken).ConfigureAwait(false);
            draft = matrix.CurrentDraft ?? throw new MatrixRefusedException(MatrixRefusal.NoDraft);
            if (string.Equals(db.Entry(draft).Property<string>(AuditColumns.CreatedBy).CurrentValue, actor, StringComparison.Ordinal))
            {
                throw new MatrixRefusedException(MatrixRefusal.MakerCannotDecide);
            }

            if (!string.Equals(reviewedContentHash, request.ContentHash, StringComparison.Ordinal))
            {
                throw new MatrixRefusedException(MatrixRefusal.DraftChanged);
            }

            // Compiled again against today's catalog: an attribute retired or changed since the proposal can't be activated.
            var compiled = await compiler.CompileAsync(MatrixCompiler.HashVersion, draft.ToDocument(), cancellationToken).ConfigureAwait(false);
            if (!compiled.Publishable)
            {
                throw new MatrixRefusedException(MatrixRefusal.OverlapsFound);
            }

            request.Approve(actor, reason, matrix, compiled.Policy.ContentHash, Now());
        }
        catch (MatrixRefusedException refusal)
        {
            await RefuseAsync(tx, matrix.Id, "approve", refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await db.SaveVersionsInOrderAsync(draft, SaveAsync, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(tx.GetDbTransaction(), VersionRecord(MatrixAuditActions.Approved, matrix, draft, request, request.DecisionReason), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    public Task<MatrixChangeRequestView> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken) =>
        DecideAsync(requestId, "reject", MatrixAuditActions.Rejected, async (request, actor) =>
        {
            request.RequireDecider(actor);
            await RequireAsync(MatrixAuditActions.ApproveAction, cancellationToken).ConfigureAwait(false);
            request.Reject(actor, reason, Now());
        }, cancellationToken);

    public Task<MatrixChangeRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken) =>
        DecideAsync(requestId, "withdraw", MatrixAuditActions.Withdrawn, async (request, actor) =>
        {
            await RequireAsync(MatrixAuditActions.ManageAction, cancellationToken).ConfigureAwait(false);
            request.Withdraw(actor, reason, Now());
        }, cancellationToken);

    public async Task<IReadOnlyList<BuiltInFactView>> ListFactsAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(MatrixAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        return [.. BuiltInFacts.All.Select(f => new BuiltInFactView(
            f.Spec.AttributeId, f.Key, f.DisplayName, f.Spec.Scope.ToString(), f.Spec.DataType.ToString(), [.. f.Spec.Operators.Order().Select(o => o.ToString())]))];
    }

    private async Task<MatrixChangeRequestView> DecideAsync(Guid requestId, string step, string action, Func<MatrixChangeRequest, string, Task> decide, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var row = await LoadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        var matrix = await LoadAsync(cancellationToken).ConfigureAwait(false) ?? throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        MatrixChangeRequest request;
        try
        {
            request = Restore(row);
            await decide(request, actor).ConfigureAwait(false);
        }
        catch (MatrixRefusedException refusal)
        {
            await RefuseAsync(tx, matrix.Id, step, refusal, requestId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        Apply(row, request);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(
            tx.GetDbTransaction(),
            Event(action, matrix.Id, AuditOutcome.Succeeded, request.DecisionReason,
                Field("requestId", request.Id.ToString()), Field("versionNumber", request.VersionNumber.ToString(CultureInfo.InvariantCulture)),
                Field("contentHash", request.ContentHash), Field("maker", request.Maker), Field("state", request.State.ToString())),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return View(request);
    }

    private async Task RequireNothingPendingAsync(Guid matrixId, CancellationToken cancellationToken)
    {
        if (await db.MatrixRequests.AnyAsync(r => r.MatrixId == matrixId && r.State == ProposedState, cancellationToken).ConfigureAwait(false))
        {
            throw new MatrixRefusedException(MatrixRefusal.AlreadyPending);
        }
    }

    private static MatrixChangeRequest Restore(MatrixChangeRequestRow row) => MatrixChangeRequest.Restore(
        row.Id, row.MatrixId, row.VersionId, row.VersionNumber, row.ContentHash, row.CompiledHash, row.Maker, row.Reason, row.ProposedAtUtc,
        row.State switch
        {
            nameof(SecondPersonState.Proposed) => SecondPersonState.Proposed,
            nameof(SecondPersonState.Approved) => SecondPersonState.Approved,
            nameof(SecondPersonState.Rejected) => SecondPersonState.Rejected,
            nameof(SecondPersonState.Withdrawn) => SecondPersonState.Withdrawn,
            _ => 0,
        },
        row.Checker, row.DecisionReason, row.DecidedAtUtc);

    private static void Apply(MatrixChangeRequestRow row, MatrixChangeRequest request)
    {
        row.State = request.State.ToString();
        row.Checker = request.Checker;
        row.DecisionReason = request.DecisionReason;
        row.DecidedAtUtc = request.DecidedAtUtc;
    }

    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    /// <summary>Bumps the matrix row's version: it serialises edits and proposals.</summary>
    private void Touch(ApprovalMatrix matrix) => db.Entry(matrix).Property(m => m.Key).IsModified = true;

    private Task RequireAsync(string action, CancellationToken cancellationToken) => RequirePersonAsync(Actor(), action, MatrixRefusal.NotAllowed, cancellationToken);

    private async Task RequirePersonAsync(string person, string action, MatrixRefusal refusal, CancellationToken cancellationToken)
    {
        if (!await permissions.CanAsync(person, action, cancellationToken).ConfigureAwait(false))
        {
            throw new MatrixRefusedException(refusal);
        }
    }

    private string Actor() =>
        actorAccessor.CurrentActor is { Length: > 0 } actor ? actor : throw new MatrixRefusedException(MatrixRefusal.NotAllowed);

    private async Task<MatrixChangeRequestRow> LoadRequestAsync(Guid id, CancellationToken cancellationToken) =>
        await db.MatrixRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new MatrixNotFoundException();

    private Task<ApprovalMatrix?> LoadAsync(CancellationToken cancellationToken) =>
        db.Matrices.Include(m => m.Versions.Where(v => v.State != MatrixVersionState.Superseded))
            .SingleOrDefaultAsync(m => m.Key == ApprovalMatrix.EnterpriseKey, cancellationToken);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || databaseProvider.IsUniqueViolation(ex) || databaseProvider.IsConcurrencyFailure(ex))
        {
            throw new MatrixConflictException("The approval matrix or its request was changed by someone else; reload it and try again.", ex);
        }
    }

    private async Task RefuseAsync(IDbContextTransaction tx, Guid? matrixId, string step, MatrixRefusedException refusal, Guid? requestId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        List<AuditPayloadField> payload = [Field("step", step), Field("refusal", (refusal.Refusal ?? MatrixRefusal.InvalidRecord).ToString())];
        if (requestId is { } id)
        {
            payload.Add(Field("requestId", id.ToString()));
        }

        await audit.AppendAsync(tx.GetDbTransaction(), Event(MatrixAuditActions.Refused, matrixId, AuditOutcome.Denied, null, [.. payload]), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The whole stored document is audited with each change: the trail alone shows every rule and route each version had.</summary>
    private AuditEventRecord VersionRecord(string action, ApprovalMatrix matrix, MatrixVersion version, MatrixChangeRequest? request, string? reason)
    {
        List<AuditPayloadField> payload =
        [
            Field("versionNumber", version.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            Field("contentHash", version.ContentHash),
            Field("compiledHash", version.CompiledHash),
            Field("editedBy", version.EditedBy),
            Field("document", version.Document),
        ];
        if (request is not null)
        {
            payload.Add(Field("requestId", request.Id.ToString()));
            payload.Add(Field("maker", request.Maker));
            payload.Add(Field("state", request.State.ToString()));
        }

        return Event(action, matrix.Id, AuditOutcome.Succeeded, reason, [.. payload]);
    }

    private AuditEventRecord Event(string action, Guid? targetId, AuditOutcome outcome, string? reason, params AuditPayloadField[] payload) => new()
    {
        Action = action,
        TargetType = MatrixAuditActions.Target,
        TargetId = targetId?.ToString(),
        Outcome = outcome,
        OccurredAtUtc = Now(),
        CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
        Reason = reason,
        Payload = payload,
    };

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    private static MatrixChangeRequestView View(MatrixChangeRequest r) => new(
        r.Id, r.VersionNumber, r.ContentHash, r.CompiledHash, r.Maker, r.Reason, r.ProposedAtUtc, r.State.ToString(), r.Checker, r.DecisionReason, r.DecidedAtUtc);

    private async Task<ApprovalMatrixView> ViewAsync(ApprovalMatrix matrix, CancellationToken cancellationToken) => new(
        matrix.Id,
        matrix.CurrentActive is { } active ? await ViewAsync(active, cancellationToken).ConfigureAwait(false) : null,
        matrix.CurrentDraft is { } draft ? await ViewAsync(draft, cancellationToken).ConfigureAwait(false) : null,
        db.Entry(matrix).Property<long>(AuditColumns.Version).CurrentValue);

    /// <summary>The version with its pre-publication check, compiled against today's catalog.</summary>
    private async Task<MatrixVersionView> ViewAsync(MatrixVersion version, CancellationToken cancellationToken)
    {
        RuleAnalysis? analysis = null;
        try
        {
            analysis = (await compiler.CompileAsync(version.Id, version.ToDocument(), cancellationToken).ConfigureAwait(false)).Analysis;
        }
        catch (MatrixRefusedException ex) when (ex.Refusal == MatrixRefusal.UnknownAttribute)
        {
            // An attribute was retired since: shown without the check, and approval refuses it.
        }

        return new MatrixVersionView(
            version.Id,
            version.VersionNumber,
            version.State.ToString(),
            version.Document,
            version.ContentHash,
            version.CompiledHash,
            version.EditedBy,
            [.. (analysis?.Overlaps ?? []).Select(o => new MatrixOverlapView(o.FirstRuleId, o.SecondRuleId, o.Priority))],
            analysis?.Truncated ?? true,
            analysis?.NeverTrueRuleIds ?? [],
            db.Entry(version).Property<long>(AuditColumns.Version).CurrentValue);
    }
}

/// <summary>
/// Selects a route from the active matrix (DL-090). Fails closed: no active matrix, a stored version that no longer compiles
/// to the hash it was approved with, or any non-matching outcome is a hold with no route.
/// </summary>
public sealed class ApprovalMatrixSelector(PolicyDbContext db, MatrixCompiler compiler) : IApprovalMatrix
{
    public const string HoldMatrixChanged = "HoldMatrixChanged";

    public async Task<MatrixSelection> SelectAsync(IReadOnlyList<MatrixFact> facts, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var active = await db.MatrixVersions.AsNoTracking()
            .Where(v => v.State == MatrixVersionState.Active)
            .Join(db.Matrices.Where(m => m.Key == ApprovalMatrix.EnterpriseKey), v => v.MatrixId, m => m.Id, (v, _) => v)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            return new MatrixSelection(MatrixSelection.HoldNoMatrix, null, null, null, null, null, [], null);
        }

        CompiledMatrix compiled;
        try
        {
            // The same rules must compile to the same hash they were approved with; a changed attribute version means not this
            // matrix. Then compiled again under the version's own id, so the evidence names it.
            var document = active.ToDocument();
            var check = await compiler.CompileAsync(MatrixCompiler.HashVersion, document, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(check.Policy.ContentHash, active.CompiledHash, StringComparison.Ordinal))
            {
                return Hold(active);
            }

            compiled = await compiler.CompileAsync(active.Id, document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MatrixRefusedException or MatrixRejectedException)
        {
            return Hold(active);
        }

        var snapshot = new AttributeSnapshot(facts.Select(f => new SnapshotEntry(
            RuleVocabulary.TrySubject(f.Subject, out var subject) ? subject : throw new ArgumentException("Unknown fact subject.", nameof(facts)),
            f.AttributeId,
            f.Value,
            f.SynchronizedAtUtc)));
        var result = PolicyEvaluator.Evaluate(compiled.Policy, snapshot, asOfUtc);
        var route = result.Outcome == RuleOutcome.Matched && result.MatchedRuleId is { } ruleId
            ? compiled.Routes[ruleId].Select(s => new MatrixRouteStage(
                s.Sequence, s.Kind.ToString(), s.Source.ToString(), s.RoleTypeCode, s.WorkgroupKey, s.Quorum.ToString(), s.MinimumApprovals,
                s.Mandatory, s.Independent, s.DeadlineHours, s.OnTimeout.ToString(), [.. s.Fields.Select(f => f.ToString())])).ToList()
            : [];
        return new MatrixSelection(
            result.Outcome.ToString(), active.Id, active.VersionNumber, active.ContentHash, active.CompiledHash, result.MatchedRuleId, route, result.Evidence.ToCanonicalJson());
    }

    private static MatrixSelection Hold(MatrixVersion active) =>
        new(HoldMatrixChanged, active.Id, active.VersionNumber, active.ContentHash, active.CompiledHash, null, [], null);
}
