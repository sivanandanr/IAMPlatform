using System.Reflection;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Administration.Domain.Access;
using IAM.Modules.Administration.Infrastructure.Access;
using Shouldly;

namespace IAM.Administration.Tests;

/// <summary>P0-013 slice 3: the permission evaluator behind every endpoint, the audit throttle and refusal mapping.</summary>
public sealed class PermissionCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ContractActionIdsAreExactlyTheCatalog()
    {
        var contract = typeof(AccessActions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);
        contract.ShouldBe(ActionCatalog.All.Select(a => a.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>Actions everyone signed in has skip the store: adding one is a security change and must be reviewed.</summary>
    [Fact]
    public void OnlyTheReviewedActionsBelongToEveryone() =>
        ActionCatalog.EveryoneActions.Order(StringComparer.Ordinal).ShouldBe([ActionCatalog.OwnPermissionsRead, ActionCatalog.SelfRead]);

    [Fact]
    public async Task UnknownActionsAndEmptySubjectsAreRefusedWithoutReadingTheStore()
    {
        var store = new FakeAccess(P.Started());
        var evaluator = new AccessPermissionEvaluator(store);
        (await evaluator.CanAsync(P.Admin, "administration.everything", Ct)).ShouldBeFalse();
        (await evaluator.CanAsync(P.Admin, "ADMINISTRATION.ROLES.READ", Ct)).ShouldBeFalse();
        (await evaluator.CanAsync(string.Empty, ActionCatalog.RolesRead, Ct)).ShouldBeFalse();
        store.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task EveryoneActionsAreAllowedWithoutReadingTheStore()
    {
        var store = new FakeAccess(P.Started());
        var evaluator = new AccessPermissionEvaluator(store);
        (await evaluator.CanAsync("anyone-signed-in", ActionCatalog.SelfRead, Ct)).ShouldBeTrue();
        (await evaluator.CanAsync("anyone-signed-in", ActionCatalog.OwnPermissionsRead, Ct)).ShouldBeTrue();
        store.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task RoleActionsComeFromTheStoredModelReadOncePerScope()
    {
        var store = new FakeAccess(P.Started());
        var evaluator = new AccessPermissionEvaluator(store);
        (await evaluator.CanAsync(P.Admin, ActionCatalog.ChangesPropose, Ct)).ShouldBeTrue();
        (await evaluator.CanAsync(P.Admin, ActionCatalog.ChangesApprove, Ct)).ShouldBeFalse();
        (await evaluator.CanAsync(P.Manager, ActionCatalog.ChangesApprove, Ct)).ShouldBeTrue();
        (await evaluator.CanAsync(P.Alice, ActionCatalog.RolesRead, Ct)).ShouldBeFalse();
        (await evaluator.CanAsync(P.Admin.ToUpperInvariant(), ActionCatalog.ChangesPropose, Ct)).ShouldBeFalse();
        store.Reads.ShouldBe(1);

        // A new request (scope) reads again, so a revoked role stops working on the next request.
        var next = new AccessPermissionEvaluator(store);
        store.Model = P.ProposeAndApprove(P.WithSecondManager(), new RemoveAssignment(P.Manager, AccessModel.IamManager), P.Admin, P.Second);
        (await next.CanAsync(P.Manager, ActionCatalog.ChangesApprove, Ct)).ShouldBeFalse();
        store.Reads.ShouldBe(2);
    }

    [Fact]
    public async Task ADamagedStoreIsNeverAGrant()
    {
        var store = new FakeAccess(P.Started()) { Failure = new AccessStoreIntegrityException("hash", "damaged") };
        var evaluator = new AccessPermissionEvaluator(store);
        await Should.ThrowAsync<AccessStoreIntegrityException>(() => evaluator.CanAsync(P.Admin, ActionCatalog.ChangesPropose, Ct));
    }

    [Fact]
    public void IntegrityAuditsAreLimitedPerKeyAndCountWhatWasLeftOut()
    {
        var clock = new SteppingClock(P.T0);
        var throttle = new AccessIntegrityAuditThrottle(clock);
        throttle.TryEnter("read:hash", out var first).ShouldBeTrue();
        first.ShouldBe(0);
        throttle.TryEnter("read:hash", out _).ShouldBeFalse();
        throttle.TryEnter("read:hash", out _).ShouldBeFalse();
        throttle.TryEnter("read:link", out _).ShouldBeTrue("a different check is a different key");

        clock.Now += AccessIntegrityAuditThrottle.Window - TimeSpan.FromTicks(1);
        throttle.TryEnter("read:hash", out _).ShouldBeFalse();
        clock.Now += TimeSpan.FromTicks(1);
        throttle.TryEnter("read:hash", out var leftOut).ShouldBeTrue();
        leftOut.ShouldBe(3);
        throttle.TryEnter("read:hash", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(AccessRefusal.NotAllowed, AccessFailureKind.Forbidden)]
    [InlineData(AccessRefusal.SelfGrant, AccessFailureKind.Forbidden)]
    [InlineData(AccessRefusal.MakerCannotDecide, AccessFailureKind.Forbidden)]
    [InlineData(AccessRefusal.NotMaker, AccessFailureKind.Forbidden)]
    [InlineData(AccessRefusal.MakerNoLongerAllowed, AccessFailureKind.Forbidden)]
    [InlineData(AccessRefusal.NotProposed, AccessFailureKind.WrongState)]
    [InlineData(AccessRefusal.AlreadyBootstrapped, AccessFailureKind.WrongState)]
    [InlineData(AccessRefusal.IncompatibleRoles, AccessFailureKind.Invalid)]
    [InlineData(AccessRefusal.SeparatedDuties, AccessFailureKind.Invalid)]
    [InlineData(AccessRefusal.WouldLockOut, AccessFailureKind.Invalid)]
    [InlineData(AccessRefusal.InvalidSubject, AccessFailureKind.Invalid)]
    public void RefusalsMapToAKindAndOnlyTheirCode(AccessRefusal refusal, AccessFailureKind kind)
    {
        AccessFailures.Classify(new AccessRefusedException(refusal), out var code).ShouldBe(kind);
        code.ShouldBe(refusal.ToString());
    }

    [Fact]
    public void OtherFailuresMapWithoutACode()
    {
        AccessFailures.Classify(new AccessStoreIntegrityException("hash", "x"), out var a).ShouldBe(AccessFailureKind.Unavailable);
        AccessFailures.Classify(new AccessConflictException(), out var b).ShouldBe(AccessFailureKind.Conflict);
        AccessFailures.Classify(new AccessChangeNotFoundException(), out var c).ShouldBe(AccessFailureKind.NotFound);
        AccessFailures.Classify(new InvalidOperationException("db"), out var d).ShouldBe(AccessFailureKind.None);
        new[] { a, b, c, d }.ShouldAllBe(x => x == null);
    }

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Only the model read is used by the evaluator; the rest is not part of these tests.</summary>
    private sealed class FakeAccess(AccessModel model) : IAccessAdministration
    {
        public AccessModel Model { get; set; } = model;

        public Exception? Failure { get; init; }

        public int Reads { get; private set; }

        public Task<AccessModel> CurrentModelAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Failure is null ? Task.FromResult(Model) : Task.FromException<AccessModel>(Failure);
        }

        public Task<ChangeRequestView?> FindChangeAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChangeRequestView> ProposeAsync(Guid id, AccessChange change, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChangeRequestView> ApproveAsync(Guid id, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChangeRequestView> RejectAsync(Guid id, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChangeRequestView> WithdrawAsync(Guid id, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task BootstrapAsync(string administrator, string manager, BootstrapEvidence? evidence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
