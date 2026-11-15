using System.Text.Json;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using Shouldly;

namespace IAM.Workflow.Tests;

/// <summary>The action log (DL-072a): every kind survives its JSON, and a replay rebuilds exactly the live instance.</summary>
public sealed class ApprovalActionLogTests
{
    private static readonly DateTimeOffset T0 = F.T0;

    /// <summary>Applies each action live, keeps it, then replays the kept log into a fresh instance.</summary>
    private static (ApprovalInstance Live, ApprovalInstance Replayed) Run(LoggedAction bound, params Func<ApprovalInstance, LoggedAction>[] steps)
    {
        var log = new List<LoggedAction> { bound };
        var live = ApprovalActionLog.Start(bound);
        foreach (var step in steps)
        {
            var action = step(live);
            ApprovalActionLog.Apply(live, action);
            log.Add(action);
        }

        // Through text, as stored.
        var stored = log.Select(a => new LoggedAction(a.Kind, new string(a.Payload.AsSpan()), a.AtUtc, a.Actor)).ToList();
        var replayed = ApprovalActionLog.Start(stored[0]);
        foreach (var action in stored.Skip(1))
        {
            ApprovalActionLog.Apply(replayed, action);
        }

        return (live, replayed);
    }

    private static void ShouldMatch(ApprovalInstance live, ApprovalInstance replayed)
    {
        replayed.History.ShouldBe(live.History);
        replayed.State.ShouldBe(live.State);
        replayed.Version.ShouldBe(live.Version);
        replayed.Decisions.Count.ShouldBe(live.Decisions.Count);
        replayed.Escalations.Count.ShouldBe(live.Escalations.Count);
        replayed.Stages.Select(s => (s.State, s.Candidates.Count, s.Resolution?.Outcome)).ShouldBe(live.Stages.Select(s => (s.State, s.Candidates.Count, s.Resolution?.Outcome)));
    }

    private static LoggedAction Supply(ApprovalInstance instance, Guid stage, DateTimeOffset at) =>
        ApprovalActionLog.Supplied(stage, F.NamedResolution(instance.Stages.Single(s => s.StageId == stage).Source.People), at);

    [Fact]
    public void DecisionsTasksAndReturnsReplay()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, kind: StageKind.Action, candidates: [F.B]), F.Stage(F.S3, 3, candidates: [F.C])]);
        var (live, replayed) = Run(
            ApprovalActionLog.Bound(F.Binding, route, T0),
            i => Supply(i, F.S1, T0),
            i => ApprovalActionLog.Decided(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, "Fine", i.Version), T0.AddMinutes(1)),
            i => Supply(i, F.S2, T0.AddMinutes(1)),
            i => ApprovalActionLog.Completed(new ActionCompletion(F.Id(2), F.S2, F.B, null, "Done", i.Version), T0.AddMinutes(2)),
            i => Supply(i, F.S3, T0.AddMinutes(2)),
            i => ApprovalActionLog.Returned(F.S3, F.C, null, "Needs a reason", i.Version, T0.AddMinutes(3)));
        live.State.ShouldBe(ApprovalState.Returned);
        ShouldMatch(live, replayed);
    }

    [Fact]
    public void EscalationsTimeoutsAndClosingReplay()
    {
        var route = ApprovalRoute.Create([
            F.Stage(F.S1, 1, mandatory: true, candidates: [F.Requester]),
            F.Stage(F.S2, 1, candidates: [F.Beneficiary]),
            F.Stage(F.S3, 2, candidates: [F.C])]);
        var (live, replayed) = Run(
            ApprovalActionLog.Bound(F.Binding, route, T0),
            i => Supply(i, F.S1, T0),
            i => Supply(i, F.S2, T0),
            i => ApprovalActionLog.Proposed(F.S1, [F.A], F.E, "Owner away", i.Version, T0.AddMinutes(1)),
            i => ApprovalActionLog.EscalationWithdrawn(F.S1, F.SecondEscalator, "Wrong list", i.Version, T0.AddMinutes(2)),
            i => ApprovalActionLog.Proposed(F.S1, [F.A, F.B], F.E, "Owner away", i.Version, T0.AddMinutes(3)),
            i => ApprovalActionLog.EscalationApproved(F.S1, [F.A, F.B], F.SecondEscalator, "Checked", i.Version, T0.AddMinutes(4)),
            i => ApprovalActionLog.Escalated(F.S2, [F.D], F.P(77), "Owner away", i.Version, T0.AddMinutes(5)),
            i => ApprovalActionLog.TimeoutsApplied(i.Version, T0.AddHours(30)),
            i => ApprovalActionLog.Cancelled(F.P(90), "Not needed", i.Version, T0.AddHours(31)));
        live.State.ShouldBe(ApprovalState.Cancelled);
        live.Escalations.Count.ShouldBe(2);
        ShouldMatch(live, replayed);
        replayed.Escalations[0].SecondReason.ShouldBe("Checked");
        replayed.EscalationWithdrawals.Single().Reason.ShouldBe("Wrong list");
    }

    [Fact]
    public void AWithdrawalReplays()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A])]);
        var (live, replayed) = Run(
            ApprovalActionLog.Bound(F.Binding, route, T0),
            i => ApprovalActionLog.Withdrawn(F.Requester, null, i.Version, T0.AddMinutes(1)));
        live.State.ShouldBe(ApprovalState.Withdrawn);
        ShouldMatch(live, replayed);
    }

    [Fact]
    public void ARefusedActionReplaysAsTheSameRefusal()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A])]);
        var instance = ApprovalActionLog.Start(ApprovalActionLog.Bound(F.Binding, route, T0));
        var refused = ApprovalActionLog.Escalated(F.S1, [default(PersonId)], F.E, "r", instance.Version, T0);
        F.Refusal(() => ApprovalActionLog.Apply(instance, refused)).ShouldBe(ApprovalRefusal.StageNotEscalatable);
        refused.Payload.ShouldContain("null", Case.Sensitive, "a default person is stored as null, never as an empty id");
    }

    [Fact]
    public void DamagedPayloadsAreRefusedNotGuessed()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A])]);
        var bound = ApprovalActionLog.Bound(F.Binding, route, T0);
        var instance = ApprovalActionLog.Start(bound);
        Should.Throw<JsonException>(() => ApprovalActionLog.Apply(instance, new LoggedAction(ApprovalActionKind.Decided, """{"decisionId":"00000000-0000-0000-0000-000000000001","extra":1}""", T0, null)));
        Should.Throw<JsonException>(() => ApprovalActionLog.Apply(instance, new LoggedAction(ApprovalActionKind.Withdrawn, """{"actor":" bad","reason":null,"expectedVersion":1}""", T0, null)));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, bound));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Start(ApprovalActionLog.TimeoutsApplied(1, T0)));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, new LoggedAction((ApprovalActionKind)99, "{}", T0, null)));
    }

    [Fact]
    public void TheStoredActorMustBeThePersonInTheInputs()
    {
        // DL-075c: the actor column can't be changed by itself, and a system action names nobody.
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A])]);
        var bound = ApprovalActionLog.Bound(F.Binding, route, T0);
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Start(bound with { Actor = F.A.Value }));

        var instance = ApprovalActionLog.Start(bound);
        var supplied = Supply(instance, F.S1, T0);
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, supplied with { Actor = F.A.Value }));
        ApprovalActionLog.Apply(instance, supplied);

        var decided = ApprovalActionLog.Decided(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, null, instance.Version), T0.AddMinutes(1));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, decided with { Actor = F.B.Value }));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, decided with { Actor = null }));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, ApprovalActionLog.TimeoutsApplied(instance.Version, T0) with { Actor = F.A.Value }));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, ApprovalActionLog.Withdrawn(F.Requester, null, instance.Version, T0) with { Actor = F.A.Value }));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, ApprovalActionLog.Escalated(F.S1, [F.B], F.E, "r", instance.Version, T0) with { Actor = F.A.Value }));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, ApprovalActionLog.Returned(F.S1, F.A, null, "r", instance.Version, T0) with { Actor = F.B.Value }));
        Should.Throw<InvalidDataException>(() => ApprovalActionLog.Apply(instance, ApprovalActionLog.Completed(new ActionCompletion(F.Id(2), F.S1, F.A, null, null, instance.Version), T0) with { Actor = F.B.Value }));

        ApprovalActionLog.Apply(instance, decided);
        instance.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public void TheRowHashCoversEveryStoredValue()
    {
        // DL-075a: the audit carries this hash, so any change to the stored row shows.
        var action = ApprovalActionLog.Withdrawn(F.Requester, "Need basic read-only-access " + new string('é', 900), 3, T0);
        var id = F.Id(1);
        var hash = ApprovalActionLog.RowHash(id, 2, action, 4);
        hash.Length.ShouldBe(64);
        hash.ShouldMatch("^[0-9a-f]{64}$");
        ApprovalActionLog.RowHash(id, 2, new LoggedAction(action.Kind, new string(action.Payload.AsSpan()), action.AtUtc, action.Actor), 4).ShouldBe(hash);
        string[] changed =
        [
            ApprovalActionLog.RowHash(F.Id(2), 2, action, 4),
            ApprovalActionLog.RowHash(id, 3, action, 4),
            ApprovalActionLog.RowHash(id, 2, action, 5),
            ApprovalActionLog.RowHash(id, 2, action with { Kind = ApprovalActionKind.Cancelled }, 4),
            ApprovalActionLog.RowHash(id, 2, action with { Payload = action.Payload + " " }, 4),
            ApprovalActionLog.RowHash(id, 2, action with { AtUtc = T0.AddTicks(10) }, 4),
            ApprovalActionLog.RowHash(id, 2, action with { Actor = null }, 4),
        ];
        changed.ShouldAllBe(h => h != hash);
        changed.Distinct().Count().ShouldBe(changed.Length);
    }

    [Fact]
    public void LoggingAnActionNamesNobody()
    {
        var action = ApprovalActionLog.Withdrawn(F.Requester, "secret", 1, T0);
        action.ToString().ShouldNotContain("person");
        action.ToString().ShouldNotContain("secret");
        action.Actor.ShouldBe(F.Requester.Value);
        ApprovalActionLog.TimeoutsApplied(1, T0).Actor.ShouldBeNull();
    }
}
