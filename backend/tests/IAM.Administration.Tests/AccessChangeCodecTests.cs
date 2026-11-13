using IAM.Modules.Administration.Domain.Access;
using IAM.Modules.Administration.Infrastructure.Access;
using Shouldly;

namespace IAM.Administration.Tests;

/// <summary>
/// A stored change must read back as exactly the change that was proposed, and anything else must be refused rather
/// than read as some other change (DL-059).
/// </summary>
public sealed class AccessChangeCodecTests
{
    public static TheoryData<AccessChange> Changes() =>
    [
        new AssignRole("alice@example.test", AccessModel.Auditor),
        new RemoveAssignment("Bob", AccessModel.PolicyAuthor),
        new DefineRole("reviewer", "Reviewer \"read\" only", [ActionCatalog.RolesRead, ActionCatalog.ChangesRead]),
        new DefineRole("empty-role", "Empty", []),
        new AddIncompatiblePair(AccessModel.Auditor, AccessModel.PolicyAuthor),
        new RemoveIncompatiblePair(AccessModel.PolicyAuthor, AccessModel.PolicyChecker),
    ];

    [Theory]
    [MemberData(nameof(Changes))]
    public void EveryKindReadsBackExactly(AccessChange change)
    {
        var (kind, payload) = AccessChangeCodec.Encode(change);
        var decoded = AccessChangeCodec.Decode(kind, payload);
        decoded.GetType().ShouldBe(change.GetType());
        if (decoded is DefineRole role && change is DefineRole original)
        {
            role.Code.ShouldBe(original.Code);
            role.Name.ShouldBe(original.Name);
            role.Actions.ShouldBe(original.Actions);
        }
        else
        {
            decoded.ShouldBe(change);
        }
    }

    [Fact]
    public void KindsAreDistinct()
    {
        Changes().Select(c => AccessChangeCodec.Encode(c.Data).Kind).Distinct().Count().ShouldBe(5);
    }

    [Theory]
    [InlineData("AssignRole", "{\"subject\":\"a\",\"roleCode\":\"auditor\",\"extra\":1}")]
    [InlineData("AssignRole", "{\"subject\":\"a\"}")]
    [InlineData("AssignRole", "{\"subject\":null,\"roleCode\":\"auditor\"}")]
    [InlineData("AssignRole", "{\"Subject\":\"a\",\"RoleCode\":\"auditor\"}")]
    [InlineData("AssignRole", "not json")]
    [InlineData("AssignRole", "null")]
    [InlineData("DefineRole", "{\"code\":\"r\",\"name\":\"R\",\"actions\":[null]}")]
    [InlineData("DefineRole", "{\"code\":\"r\",\"name\":\"R\"}")]
    [InlineData("DefineRole", "{\"code\":\"r\",\"name\":\"R\",\"actions\":[[[[[\"x\"]]]]]}")]
    [InlineData("AddIncompatiblePair", "{\"first\":\"a\"}")]
    [InlineData("RemoveIncompatiblePair", "{\"first\":\"a\",\"second\":\"b\",\"third\":\"c\"}")]
    [InlineData("AssignRole", "{\"subject\":\"alice\",\"subject\":\"mallory\",\"roleCode\":\"auditor\"}")]
    [InlineData("AddIncompatiblePair", "{\"first\":\"a\",\"second\":\"b\",\"second\":\"c\"}")]
    [InlineData("assignrole", "{\"subject\":\"a\",\"roleCode\":\"auditor\"}")]
    [InlineData("GrantEverything", "{}")]
    [InlineData("", "{}")]
    public void AnythingElseIsRefused(string kind, string payload)
    {
        Should.Throw<AccessRefusedException>(() => AccessChangeCodec.Decode(kind, payload)).Refusal.ShouldBe(AccessRefusal.InvalidChange);
    }

    [Fact]
    public void OversizedPayloadsAreRefusedBeforeParsing()
    {
        var payload = "{\"subject\":\"" + new string('a', AccessChangeCodec.MaxPayloadLength) + "\",\"roleCode\":\"auditor\"}";
        Should.Throw<AccessRefusedException>(() => AccessChangeCodec.Decode(AccessChangeCodec.KindAssignRole, payload)).Refusal.ShouldBe(AccessRefusal.InvalidChange);
    }
}
