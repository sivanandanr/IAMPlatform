# ADR-0027: Approver resolution when a level opens
Status: Proposed (decisions taken by the owner as DL-069, 2026-11-14) — stage 4 slice 2 implemented; human sign-off pending | Date: 2026-11-14 | Decision owners: IAM Domain / Workflow lead, Security Architect (named approvers TBD)

Source: IAM-WF-003 (approver sources), IAM-WF-006 (fallback; expiry never grants), IAM-WF-011 (workgroups), AR-2.5 (application role as an approver type), AR-2.6 as changed by DL-057b (no skipping), AR-2.7 (role holders can't act on their own request or access), DL-052 (empty required role blocks), DL-057, DL-059d (people are sign-in ids until a person directory exists), DL-068, DL-069; ADR-0005, ADR-0025, ADR-0026; CLAUDE.md rules 3 and 5. Drafted by Claude Code.

## Context and requirements
Until now the approval engine (ADR-0005) took every stage's people when a request was bound, already resolved (R-032), and identified people by Guid while the catalog and workgroups use sign-in ids. DL-068 says approvers are found when a level opens, from Named user, Workgroup, Application owner and Application role, with evidence; anything that finds nobody must hold, never approve or skip (rule 3).

## Options considered
1. Resolve every level when the request is bound (simpler, but a later level uses people who may have left the role).
2. A stage carries its approver source and waits when it opens; the application layer resolves and supplies the people with evidence (DL-069b).
3. Keep Guid people in the engine and map sign-in ids to them later (nothing to map from until a person directory exists).

## Decision and rationale
Option 2, with sign-in ids everywhere (DL-069a).
- **`PersonId`** (BuildingBlocks.Abstractions): one validated sign-in id, compared exactly, that never prints itself. The engine's actors, candidates, requester, beneficiary and policy maker use it.
- **Stages carry an `ApproverSource`**: NamedUsers (people), Workgroup (key), ApplicationOwner (application), ApplicationRole (application and role type), or Manager, ManagerOfManager, EntitlementOwner, Matrix (no fields; no data yet). A route refuses a source without exactly the fields its kind needs. A minimum-N quorum is checked against named people now, and against who was found when the level opens.
- **Waiting levels**: when a step opens, each stage enters `AwaitingApprovers`; its deadline starts and its timeout applies, but nobody can decide, return or escalate it. `SupplyApprovers(stage, resolution, version, now)` accepts one resolution, once, before the deadline. A `Resolved` outcome makes the stage active with those people; any other outcome holds it. The engine refuses (`InvalidResolution`) a resolution whose first step is not the level's source, that names an excluded person or anyone twice, that names people without being resolved, that claims "unavailable" for a source with data or "required role empty" for anything but a role, or whose evidence is malformed. Carrying the same approver (DL-057c, DL-058) happens when the people are supplied.
- **The chain (`ApproverResolver`, Workflow.Application)**: the level's own source; for an application role that is empty and optional, missing, or whose holders are all excluded, the application owner; then, for any source that left nobody, the fallback workgroup (`Workflow:FallbackWorkgroup`, default `iam_managers`, DL-068c); then hold (`NobodyAvailable`). The requester, the person getting access and the policy maker are removed at every step (AR-2.7, DL-069c). An empty required role holds (`RequiredRoleEmpty`, DL-052) and is filled by nobody else. Manager, manager of manager, entitlement owner and matrix answer `SourceUnavailable` and hold with no fallback, so IAM managers never stand in for them (DL-069d). Missing applications, roles or workgroups, and a workgroup with a damaged member id, are steps in the evidence, never errors and never people.
- **Evidence (DL-069e)**: each step records its source, its result, a reference (workgroup key, or application and role type ids), the active version number and content hash read, and how many people it found and excluded. No names. The engine keeps the resolution on the stage.
- **Contracts**: Catalog's new `IApplicationApproverDirectory` returns an application's active owner and roles with their version and hash; Organization's `IWorkgroupDirectory` now also returns the member list's content hash. Both read active data only, without tracking. Workflow references only these Contracts.

## Security and privacy impact
- A level gets people only from a resolution the engine checked; a failed resolution, a missing source, a timeout while waiting, or an empty fallback all hold. Nothing here can approve.
- Excluded people are removed by the resolver and refused by the engine, so the rule holds even if a caller builds its own resolution.
- Remaining risks (R-036): no workflow storage yet, so nothing persists or audits a resolution; escalation conflicts are checked against the people known so far, and a pending level's people are known only for named users; a resolution reflects data at the moment it was read, and a later catalog or workgroup change does not change an open level; sign-in ids are compared exactly (R-035).

## SQL Server vs PostgreSQL impact
No schema change. The two directories run the same queries on both engines (`ApplicationCatalogParityTests`, `WorkgroupParityTests`).

## Hybrid deployment impact
None.

## Operational consequences and rollback
`Workflow:FallbackWorkgroup` may name another workgroup; a malformed key fails when the resolver is first used. Someone must propose and approve members for the fallback workgroup, or every level that falls back holds. No migration: rollback is reverting the code.

## Tests / evidence
`IAM.Workflow.Tests` (`ApproverSupplyTests`, `ApproverResolverTests`, and the engine suite run with every level waiting and supplied), `Parity/ApplicationCatalogParityTests.TheApproverDirectoryReadsOnlyActiveSettingsAndFeedsResolution`, `Parity/WorkgroupParityTests`. Results in the PR.

## Open questions and approvals
- Who may choose approvers (escalation permission, DL-048): slice 3.
- Workflow storage, auditing each resolution, and calling the resolver when a stored level opens: later stage.
- Manager and matrix sources once their data exists; the person directory (DL-059d).
- Independent security review and human sign-off.
