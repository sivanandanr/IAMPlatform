# Additional requirements: portal as an application, application roles, editable action steps

Paste everything below the line into Claude in the IAM Portal project.

---

<context>
These are additional requirements for the IAM Portal (identity governance product on Azure SQL Managed Instance, ASP.NET Core with EF Core, Azure portal layout with Clarity styling).
They add to what is already built or under way. They do not replace anything.
Follow every rule in CLAUDE.md, especially: row-level security by tenant, every change audited with actor and reason, no passwords or secrets stored, second person for sensitive changes, published versions are immutable, closed by default, a timeout never grants access, nobody approves their own request.
If part of this already exists, extend it. Don't build a second copy.
</context>

<how_to_work>
1. Read CLAUDE.md, requirements.md and the current schema first.
2. Before changing anything, reply with a short plan: what you'll add, which tables and screens change, and any conflict with existing rules. Wait for my approval.
3. After approval, build in this order: database migration and tests, then prototype, then spec and md files.
4. Everything must be backward compatible. With nothing configured, the product behaves exactly as today.
5. Finish with what changed, what was tested, and what is still open.
</how_to_work>

<requirements>

## AR-1 The IAM Portal is governed like any other application

**Why:** portal roles such as IAM Administrator can change workflows, connectors and who approves what. Auditors ask who holds them, who approved them and when they were last reviewed. Today they are given directly and never reviewed.

**Switch:** optional per tenant. Off by default. Turning it on needs a second person, through `Core.ChangeApproval`. With it off, nothing changes.

Requirements:
- AR-1.1 The portal is registered as an application (for example "Acme Access") with a built-in connector type (`IAM_PORTAL`). It reads and writes portal role assignments directly. It needs no agent.
- AR-1.2 Portal roles become entitlements of that application, in two kinds:
  - **Comes with your job:** Portal user, Portal manager (from the HR feed), Application owner and Entitlement owner (from ownership). These can't be requested.
  - **Requestable:** IAM Administrator, Auditor, Helpdesk, Workflow designer. The tenant can change this list.
- AR-1.3 The application owner is outside the IAM team (default: the CISO). The owner can't hold IAM Administrator.
- AR-1.4 Privileged portal roles are just-in-time only. They switch on in a separate admin session with a fresh MFA check. That session counts as the secondary portal account.
- AR-1.5 A requestable portal role can only be held with a basis: an approved request, an emergency grant, an active activation, or a grant that existed when the feature was turned on. Anything else is refused.
- AR-1.6 Discovery checks the portal every hour. A role given directly in the database is reported as "Granted outside IAM" to the owner.
- AR-1.7 Lockout protection: the last person able to use IAM Administrator can't be removed, by a request, a review or a direct change. Emergency accounts are never suspended automatically. Removing one needs a second person.
- AR-1.8 One approval, not two: the workflow approval counts as the second person for a portal role change.
- AR-1.9 Conflict rule: IAM Administrator can't be held with Auditor. This includes eligibility for just-in-time access.
- AR-1.10 The owner reviews portal roles every quarter. Nobody reviews their own access.

Acceptance:
- With the switch off, no portal application, roles or reviews appear anywhere.
- A request for Auditor goes from manager to owner, and is set up automatically.
- A role inserted directly in the database is refused, or reported by discovery.
- Removing the last IAM Administrator fails with a clear message.

## AR-2 Optional application roles: User Admin, IT Admin and more

**Why:** some applications need named people besides the owner. They may approve, or do the work by hand.

Requirements:
- AR-2.1 There is a catalog of application roles. User Admin and IT Admin are standard. The tenant can add more, such as Security Contact or Vendor Contact. Use a catalog table, not fixed columns.
- AR-2.2 For each application, each role can be held by a person or a group, and is marked **Optional** or **Required for this application**.
- AR-2.3 If a required role is empty, the application can't be activated, and can't be requested.
- AR-2.4 The application owner stays required and single. Application roles are additional.
- AR-2.5 Workflows get a new approver type, **Application role**, next to Manager, Org Head, Application owner and Entitlement owner. Any level can use it, whether it is an approval or action only.
- AR-2.6 If an optional role is empty when a request reaches the level, the level setting decides: **skip the level** (the reason is recorded) or **ask the application owner**. It is never approved by default.
- AR-2.7 The holder of an application role can't act on a request they raised, or one that gives them access.
- AR-2.8 Ownership succession and ownership reviews also cover application roles.
- AR-2.9 Application roles are set in application settings, not the onboarding wizard. The wizard asks only for roles marked Required.

Acceptance:
- An application with no application roles behaves exactly as today.
- A level set to IT Admin routes to the IT Admin group of that application.
- An empty optional role skips the level, with the reason shown in the timeline.

## AR-3 Action-only levels can enter or change chosen details

**Why:** for an application with no connector, someone creates the account by hand and must record it. For AD service, shared and secondary accounts, the admin may need to change the generated account name. For an entitlement create request, the approver may need to change the technical name.

Requirements:
- AR-3.1 A workflow level can list **fields this level can fill in or change**, and mark each one Required, Editable or Read-only. The fields come from the existing provisioning fields. Provisioning fields work out the proposed value, and the workflow decides who may change it.
- AR-3.2 Only names and identifiers can be changed: account name, login, entitlement technical name, location such as the AD OU, and notes. A level can never add access, change who gets it, extend dates or raise privilege. A bigger change uses **Return for changes** back to the requester.
- AR-3.3 Every value is checked before submit: the naming rule, and uniqueness against existing accounts or entitlements in that application.
- AR-3.4 The proposed value, the final value, who changed it, when, and the reason are all stored and audited. The requester sees the final value in their timeline.
- AR-3.5 Never a password. Any password-like field is refused. Passwords stay with the vault and the one-time link.
- AR-3.6 The person filling in details can't be the requester or the person getting the access.
- AR-3.7 Applications with no connector get a setting for manual work: **Ticket system** (as today) or **IAM task**. An IAM task is an action-only level, usually for the User Admin, where details are entered in the portal.
- AR-3.8 Details entered by a person are a claim. If discovery can read the application, it verifies them. If it can't, the account shows **"Entered by User Admin, not verified"** and is included in the next access review.
- AR-3.9 Editing a name after earlier approvals doesn't need re-approval, because only names can change.

Acceptance:
- A level with no editable fields behaves exactly as today.
- The IT Admin changes svc-backup-rpt to svc-backup-reports. The account is created with the new name, and the audit trail shows both values.
- A duplicate name, or one that breaks the naming rule, is refused with a fix-it message.
- A User Admin task for an application with no connector records the account. It shows as not verified until discovery or a review confirms it.

</requirements>

<decisions>
These defaults are already decided. Tell me if one conflicts with something built.
- D1 Portal owner: CISO.
- D2 Just-in-time admin session counts as the secondary portal account.
- D3 User Admin and IT Admin can approve as well as do the work, set per level.
- D4 A required application role that is empty blocks the application from requests.
- D5 Applications with no connector offer both Ticket system and IAM task, chosen per application.
- D6 Changing a name doesn't send it back for re-approval.
</decisions>

<deliverables>
- A database migration for each requirement (AR-1, AR-2, AR-3), following the database steps in CLAUDE.md, with error text in `Core.ErrorMessage`, labels in `Core.EnumValue`, and a test file each, listed in README.md.
- Prototype screens: the portal in Applications; application settings with application roles; the workflow level editor with approver type Application role and editable fields; the action-only task form showing proposed and final values; the not-verified account state.
- A spec section and requirements.md entries for AR-1 to AR-3, marked Proposed until I approve.
- Updates to CLAUDE.md as listed in md-file-updates.md.
- Don't claim SQL was tested unless it ran on SQL Server.
</deliverables>
