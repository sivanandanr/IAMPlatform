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
    public const string InstancesCancel = "workflow.instances.cancel";
    public const string InstancesView = "workflow.instances.view";
    public const string InstancesRead = "workflow.instances.read";
    public const string ApprovalsAct = "workflow.approvals.act";
    public const string AttributesRead = "identity.attributes.read";
    public const string AttributesManage = "identity.attributes.manage";
    public const string AttributesApprove = "identity.attributes.approve";
    public const string ApplicationsRead = "catalog.applications.read";
    public const string ApplicationsManage = "catalog.applications.manage";
    public const string ApplicationsApprove = "catalog.applications.approve";
    public const string WorkgroupsRead = "organization.workgroups.read";
    public const string WorkgroupsManage = "organization.workgroups.manage";
    public const string WorkgroupsApprove = "organization.workgroups.approve";
    public const string EntitlementsRead = "catalog.entitlements.read";
    public const string EntitlementsManage = "catalog.entitlements.manage";
    public const string EntitlementsApprove = "catalog.entitlements.approve";
    public const string EntitlementOwnershipAccept = "catalog.entitlements.accept-ownership";
    public const string AccountsRead = "account.accounts.read";
    public const string AccountsManage = "account.accounts.manage";
    public const string AccountsApprove = "account.accounts.approve";
    public const string MatrixRead = "policy.matrix.read";
    public const string MatrixManage = "policy.matrix.manage";
    public const string MatrixApprove = "policy.matrix.approve";
    public const string RequestItemsSubmit = "request.items.submit";
    public const string RequestItemsForOthers = "request.items.request-for-others";
    public const string RequestItemsRead = "request.items.read";
    public const string RequestItemsNameAccount = "request.items.name-account";
}
