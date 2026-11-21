using IAM.BuildingBlocks.Messaging;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Domain.Items;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using Shouldly;

namespace IAM.Persistence.Tests.Request;

/// <summary>Request item rules, and how Workflow reads a submitted item, without a database (DL-091, spec v3.2 §5.3, §6.4).</summary>
public sealed class RequestItemDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void AccessForIsTakenFromTheEntitlement()
    {
        RequestItem.Duration("NoExpiry", null, null, null, null).ShouldBeNull();
        RequestItem.Duration("FixedDays", 14, null, null, null).ShouldBe(14);
        RequestItem.Duration("RequesterChooses", null, 30, 90, null).ShouldBe(30);
        RequestItem.Duration("RequesterChooses", null, 30, 90, 90).ShouldBe(90);
        foreach (var (mode, requested) in new[] { ("NoExpiry", (int?)5), ("FixedDays", 14), ("RequesterChooses", 91), ("RequesterChooses", 0), ("Other", null) })
        {
            Should.Throw<RequestRefusedException>(() => RequestItem.Duration(mode, 14, 30, 90, requested), $"{mode} {requested}").Refusal.ShouldBe(RequestRefusal.InvalidDuration);
        }
    }

    [Fact]
    public void AnItemNamesExactlyOneTargetAndIsCompleteWhenFrozen()
    {
        RequestItem Freeze(Guid? account, NewAccount? newAccount, string requester = "ann", int? days = 30) => RequestItem.Freeze(
            Guid.CreateVersion7(), requester, "ann", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Hash, account, newAccount, days,
            Guid.CreateVersion7(), Hash, Guid.CreateVersion7(), "matrix-author", "{}", "[]", Now);

        Freeze(Guid.CreateVersion7(), null).State.ShouldBe(RequestItemState.Submitted);
        Freeze(null, new NewAccount("Secondary", null)).NewAccountName.ShouldBeNull();
        foreach (var bad in new Func<RequestItem>[]
        {
            () => Freeze(null, null),
            () => Freeze(Guid.CreateVersion7(), new NewAccount("Primary", null)),
            () => Freeze(Guid.Empty, null),
            () => Freeze(null, new NewAccount("User", null)),
            () => Freeze(null, new NewAccount("Service", "bad name")),
            () => Freeze(Guid.CreateVersion7(), null, requester: " ann"),
            () => Freeze(Guid.CreateVersion7(), null, days: 0),
        })
        {
            Should.Throw<RequestRefusedException>(bad).Refusal.ShouldBe(RequestRefusal.InvalidRecord);
        }
    }

    [Fact]
    public void WorkflowBuildsExactlyTheRouteTheItemNamed()
    {
        var entitlement = Guid.CreateVersion7();
        var application = Guid.CreateVersion7();
        var item = Submitted(
            new SubmittedStage(Guid.CreateVersion7(), 1, "Decision", "EntitlementOwner", null, null, null, entitlement, "AnyOne", null, false, false, 72, "Escalate", []),
            new SubmittedStage(Guid.CreateVersion7(), 2, "Decision", "Workgroup", "iam_security", null, null, null, "All", null, true, true, 48, "Reject", []));
        var (binding, route) = RequestItemApprovals.Build(item);
        binding.InstanceId.ShouldBe(item.ItemId);
        binding.RequestItemId.ShouldBe(item.ItemId);
        binding.PolicyVersionId.ShouldBe(item.MatrixVersionId);
        binding.PolicyHash.ShouldBe(item.MatrixContentHash);
        (route.Stages[0].Approvers.Kind, route.Stages[0].Approvers.EntitlementId).ShouldBe((ApproverSourceKind.EntitlementOwner, (Guid?)entitlement));
        (route.Stages[1].Approvers.Kind, route.Stages[1].Approvers.WorkgroupKey).ShouldBe((ApproverSourceKind.Workgroup, "iam_security"));
        route.Stages[1].Mandatory.ShouldBeTrue();
        route.Stages[1].Deadline.ShouldBe(TimeSpan.FromHours(48));

        // Anything malformed is never processed: no route is guessed or repaired.
        foreach (var bad in new[]
        {
            Submitted(new SubmittedStage(Guid.CreateVersion7(), 1, "Decision", "EntitlementOwner", null, application, null, entitlement, "AnyOne", null, false, false, 72, "Escalate", [])),
            Submitted(new SubmittedStage(Guid.CreateVersion7(), 1, "Decision", "Manager", null, null, null, null, "AnyOne", null, false, false, 72, "Escalate", [])),
            Submitted(new SubmittedStage(Guid.CreateVersion7(), 1, "Decision", "Workgroup", "iam_security", null, null, null, "AnyOne", null, false, false, 72, "Approve", [])),
            Submitted(new SubmittedStage(Guid.CreateVersion7(), 1, "Decision", "Workgroup", "iam_security", null, null, null, "AnyOne", null, false, false, 0, "Escalate", [])),
            Submitted(),
            Submitted(new SubmittedStage(Guid.CreateVersion7(), 1, "Decision", "ApplicationOwner", null, application, null, null, "AnyOne", null, false, false, 72, "Escalate", [])) with { Requester = " ann" },
        })
        {
            Should.Throw<InvalidMessageException>(() => RequestItemApprovals.Build(bad));
        }
    }

    private static RequestItemSubmitted Submitted(params SubmittedStage[] stages) =>
        new(Guid.CreateVersion7(), "ann", "bob", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Hash, Guid.CreateVersion7(), "matrix-author", stages);
}
