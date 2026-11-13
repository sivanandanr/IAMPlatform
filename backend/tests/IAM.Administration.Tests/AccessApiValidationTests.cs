using IAM.Modules.Administration.Endpoints;
using Shouldly;

namespace IAM.Administration.Tests;

/// <summary>P0-013 slice 4: shape checks on the admin API bodies (BACKEND_INPUT_VALIDATION step 6). The domain re-checks every rule.</summary>
public sealed class AccessApiValidationTests
{
    private static readonly ProposeChangeBodyValidator Propose = new();

    public static TheoryData<ProposeChangeBody> ValidBodies() =>
    [
        new ProposeChangeBody("AssignRole", "Cover", Subject: "alice@example.test", RoleCode: "auditor"),
        new ProposeChangeBody("RemoveAssignment", "Leaver", Subject: "bob", RoleCode: "policy-author"),
        new ProposeChangeBody("DefineRole", "New role", Code: "reviewer", Name: "Reviewer <b>", Actions: ["administration.roles.read"]),
        new ProposeChangeBody("DefineRole", "Empty role", Code: "empty", Name: "Empty", Actions: []),
        new ProposeChangeBody("AddIncompatiblePair", "SoD", First: "auditor", Second: "policy-author"),
        new ProposeChangeBody("RemoveIncompatiblePair", "SoD", First: "auditor", Second: "policy-author"),
    ];

    [Theory]
    [MemberData(nameof(ValidBodies))]
    public void EachKindWithOnlyItsFieldsIsAccepted(ProposeChangeBody body) =>
        Propose.Validate(body).IsValid.ShouldBeTrue();

    public static TheoryData<ProposeChangeBody, string> InvalidBodies() => new()
    {
        { new(null, "r", Subject: "a", RoleCode: "auditor"), "Kind" },
        { new("assignrole", "r", Subject: "a", RoleCode: "auditor"), "Kind" },
        { new("GrantEverything", "r", Subject: "a", RoleCode: "auditor"), "Kind" },
        { new("AssignRole", null, Subject: "a", RoleCode: "auditor"), "Reason" },
        { new("AssignRole", new string('r', 1001), Subject: "a", RoleCode: "auditor"), "Reason" },
        { new("AssignRole", "r", Subject: null, RoleCode: "auditor"), "Subject" },
        { new("AssignRole", "r", Subject: " a", RoleCode: "auditor"), "Subject" },
        { new("AssignRole", "r", Subject: new string('a', 201), RoleCode: "auditor"), "Subject" },
        { new("AssignRole", "r", Subject: "a", RoleCode: "Auditor"), "RoleCode" },
        { new("AssignRole", "r", Subject: "a", RoleCode: "auditor'; DROP TABLE x;--"), "RoleCode" },
        { new("AssignRole", "r", Subject: "a", RoleCode: "auditor", Name: "extra"), "Name" },
        { new("AssignRole", "r", Subject: "a", RoleCode: "auditor", Actions: ["administration.changes.approve"]), "Actions" },
        { new("DefineRole", "r", Code: "x", Name: "X", Actions: null), "Actions" },
        { new("DefineRole", "r", Code: "x", Name: "X", Actions: ["a", "a"]), "Actions" },
        { new("DefineRole", "r", Code: "x", Name: "X", Actions: [.. Enumerable.Range(0, 33).Select(i => $"a{i}")]), "Actions" },
        { new("DefineRole", "r", Code: "x", Name: "X", Actions: [new string('a', 101)]), "Actions[0]" },
        { new("DefineRole", "r", Code: "x", Name: new string('n', 201), Actions: []), "Name" },
        { new("DefineRole", "r", Code: "x", Name: "X", Actions: [], Subject: "someone"), "Subject" },
        { new("AddIncompatiblePair", "r", First: "a", Second: null), "Second" },
        { new("AddIncompatiblePair", "r", First: "a", Second: "b", RoleCode: "c"), "RoleCode" },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public void AnythingElseIsRefusedOnTheRightField(ProposeChangeBody body, string field)
    {
        var result = Propose.Validate(body);
        result.IsValid.ShouldBeFalse();
        result.Errors.Select(e => e.PropertyName).ShouldContain(field);
    }

    [Fact]
    public void DecisionReasonsAreBounded()
    {
        var validator = new DecisionBodyValidator();
        validator.Validate(new DecisionBody(null)).IsValid.ShouldBeTrue();
        validator.Validate(new DecisionBody(new string('r', 1000))).IsValid.ShouldBeTrue();
        validator.Validate(new DecisionBody(new string('r', 1001))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void RejectionsNeedABoundedReason()
    {
        var validator = new RejectBodyValidator();
        validator.Validate(new RejectBody(null)).IsValid.ShouldBeFalse();
        validator.Validate(new RejectBody(string.Empty)).IsValid.ShouldBeFalse();
        validator.Validate(new RejectBody("   ")).IsValid.ShouldBeFalse();
        validator.Validate(new RejectBody("Not needed")).IsValid.ShouldBeTrue();
        validator.Validate(new RejectBody(new string('r', 1001))).IsValid.ShouldBeFalse();
    }
}
