namespace IAM.Modules.Administration.Contracts;

/// <summary>
/// Action ids that endpoints in any module name with <c>RequirePermission</c> (ADR-0008, P0-013). The master list with
/// descriptions is <c>ActionCatalog</c> in the Administration domain; a test keeps the two identical, and the API test
/// refuses any endpoint that names an action not in the catalog.
/// </summary>
public static class AccessActions
{
    public const string SelfRead = "identity.self.read";
    public const string OwnPermissionsRead = "administration.permissions.read-own";
    public const string RolesRead = "administration.roles.read";
    public const string ChangesRead = "administration.changes.read";
    public const string ChangesPropose = "administration.changes.propose";
    public const string ChangesApprove = "administration.changes.approve";
    public const string ApprovalsEscalate = "workflow.approvals.escalate";
    public const string AttributesRead = "identity.attributes.read";
    public const string AttributesManage = "identity.attributes.manage";
    public const string AttributesApprove = "identity.attributes.approve";
}
