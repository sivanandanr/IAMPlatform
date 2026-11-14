using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.Modules.Organization.Domain.Workgroups;
using Shouldly;

namespace IAM.Persistence.Tests.Organization;

/// <summary>Workgroup rules without a database (DL-068, spec 1.8, IAM-WF-011).</summary>
public sealed class WorkgroupDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The Workgroups migrations seed the fallback workgroup's empty draft with this hash, computed outside .NET.</summary>
    [Fact]
    public void TheSeededFallbackDraftHashMatchesTheDomain() =>
        WorkgroupVersion.ComputeContentHash(WorkgroupRules.FallbackKey, 1, []).ShouldBe("66aca7e3a7e5de11db95a6b17e4e8c47bddcc5cbabda2e57fa3f61f82f5ea2a4");

    [Fact]
    public void MembersArePeopleEachOnceAtMost256()
    {
        WorkgroupRules.ValidateMembers(["b", "a"]).ShouldBe(["a", "b"]);
        WorkgroupRules.ValidateMembers([]).ShouldBeEmpty();
        foreach (var bad in new IReadOnlyList<string?>[] { ["a", "a"], [null], [" a"], ["a‮"], [.. Enumerable.Range(0, 257).Select(i => $"p{i}")] })
        {
            Should.Throw<WorkgroupRejectedException>(() => WorkgroupRules.ValidateMembers(bad)).Field.ShouldBe("Members");
        }

        Should.Throw<WorkgroupRejectedException>(() => WorkgroupRules.ValidateMembers(null)).Field.ShouldBe("Members");
    }

    [Theory]
    [InlineData("iam_managers", true)]
    [InlineData("finance_approvers", true)]
    [InlineData("iam-managers", false)]
    [InlineData("I", false)]
    [InlineData("grp\n", false)]
    public void KeysAreLowerCaseCodes(string key, bool valid) => WorkgroupRules.IsKey(key).ShouldBe(valid);

    [Fact]
    public void TheHashCoversMembersButNotTheirOrder()
    {
        var hash = WorkgroupVersion.ComputeContentHash("wg", 1, ["a", "b"]);
        WorkgroupVersion.ComputeContentHash("wg", 1, ["b", "a"]).ShouldBe(hash);
        WorkgroupVersion.ComputeContentHash("wg", 1, ["a"]).ShouldNotBe(hash);
        WorkgroupVersion.ComputeContentHash("wg", 2, ["a", "b"]).ShouldNotBe(hash);
        WorkgroupVersion.ComputeContentHash("wg", 1, ["a", "B"]).ShouldNotBe(hash);
    }

    [Fact]
    public void ASecondPersonWhoIsNotAMemberActivatesExactlyWhatWasProposed()
    {
        var workgroup = Workgroup.Register("finance_approvers", "Finance approvers", ["alice", "bob"]);
        var request = WorkgroupChangeRequest.Propose(Guid.NewGuid(), workgroup, "maker", "New group", Now);
        Should.Throw<WorkgroupRefusedException>(() => request.Approve("maker", null, workgroup, Now)).Refusal.ShouldBe(WorkgroupRefusal.MakerCannotDecide);
        Should.Throw<WorkgroupRefusedException>(() => request.Approve("alice", null, workgroup, Now)).Refusal.ShouldBe(WorkgroupRefusal.MemberCannotDecide);
        Should.Throw<WorkgroupRefusedException>(() => request.Reject("bob", "No", workgroup, Now)).Refusal.ShouldBe(WorkgroupRefusal.MemberCannotDecide);
        request.State.ShouldBe(SecondPersonState.Proposed);

        request.Approve("checker", null, workgroup, Now);
        workgroup.CurrentActive.ShouldNotBeNull().People.ShouldBe(["alice", "bob"]);
        workgroup.CurrentDraft.ShouldBeNull();
    }

    [Fact]
    public void ARefusedApprovalChangesNothingAndAnEditedDraftCantBeActivated()
    {
        var workgroup = Workgroup.Register("hr_approvers", "HR", ["alice"]);
        var request = WorkgroupChangeRequest.Propose(Guid.NewGuid(), workgroup, "maker", "Ready", Now);
        workgroup.UpdateDraft(["alice", "eve"]).ShouldBeTrue();
        Should.Throw<WorkgroupRefusedException>(() => request.Approve("checker", null, workgroup, Now)).Refusal.ShouldBe(WorkgroupRefusal.DraftChanged);
        request.State.ShouldBe(SecondPersonState.Proposed);
        workgroup.CurrentDraft.ShouldNotBeNull().State.ShouldBe(MemberVersionState.Draft);
    }

    [Fact]
    public void ANewMemberListSupersedesTheActiveOne()
    {
        var workgroup = Workgroup.Register("ops", "Ops", ["alice"]);
        Should.Throw<WorkgroupRefusedException>(() => workgroup.StartNewDraft(["bob"], 1)).Refusal.ShouldBe(WorkgroupRefusal.DraftExists);
        WorkgroupChangeRequest.Propose(Guid.NewGuid(), workgroup, "maker", "Ready", Now).Approve("checker", null, workgroup, Now);
        workgroup.StartNewDraft(["bob"], 1).VersionNumber.ShouldBe(2);

        // Someone leaving the group can approve the list without them: only the members of the named list are barred.
        WorkgroupChangeRequest.Propose(Guid.NewGuid(), workgroup, "maker", "Swap", Now).Approve("alice", null, workgroup, Now);
        workgroup.CurrentActive.ShouldNotBeNull().People.ShouldBe(["bob"]);
        workgroup.Versions.Single(v => v.VersionNumber == 1).State.ShouldBe(MemberVersionState.Superseded);
    }

    [Fact]
    public void ARenameIsNotMembership()
    {
        var workgroup = Workgroup.Register("ops2", "Ops", []);
        workgroup.Rename("Ops").ShouldBeFalse();
        workgroup.Rename("Operations").ShouldBeTrue();
        Should.Throw<WorkgroupRejectedException>(() => workgroup.Rename("")).Field.ShouldBe("DisplayName");
    }
}
