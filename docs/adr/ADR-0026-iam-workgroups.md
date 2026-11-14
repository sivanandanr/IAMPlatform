# ADR-0026: IAM workgroups — internal approval groups managed with a second person
Status: Proposed (decisions taken by the owner as DL-068, 2026-11-14) — stage 4 slice 1 implemented; human sign-off pending | Date: 2026-11-14 | Decision owners: IAM Domain lead, Security Architect (named approvers TBD)

Source: Spec v3.2 Module 1.8 ("Workgroups and approver groups are internal groups. They are used for ownership and approval, not for application access."), IAM-WF-003 (workgroup as an approver source), IAM-WF-006 (fallback workgroup), IAM-WF-011 (workgroup semantics; members who are requesters or beneficiaries can't self-approve); DL-057b (fallback group, IAM Manager by default), DL-066a, DL-068; CLAUDE.md rules 3, 4 and 5. Drafted by Claude Code.

## Context and requirements
Stage 4 resolves approvers when a level opens (IAM-WF-003). Workgroups are one source and the fallback for a level with nobody able to act (DL-057b, IAM-WF-006). Changing a workgroup's members changes who approves, so it is a sensitive change (rule 4).

## Options considered
1. Directory groups read from connectors (DL-066a as first decided).
2. IAM workgroups owned by the Organization module, members changed with a second person.
3. People holding an access role (for the fallback only).

## Decision and rationale
Option 2 (DL-068). The spec defines approval groups as internal; there is no directory connector yet; access roles describe what people may administer, not who approves requests.
- **Workgroup** (`organization.Workgroup`): an immutable key and a display name; never deleted. Renames need `organization.workgroups.manage`, are audited and are refused while a change is pending.
- **Member lists** are numbered versions (Draft → Active → Superseded) of people (sign-in ids), at most 256. A draft becomes active only through a request approved by a second person who is not the maker, not the draft's creator or last editor, and **not a member of the list** (`MemberCannotDecide`); the approver names the content hash they reviewed; the draft is frozen while pending; edits and proposals are serialised on the workgroup row. The same design and review fixes as the application catalog (ADR-0025, DL-067).
- **Fallback workgroup** `iam_managers` (DL-068c) is seeded empty (an empty draft, fixed id) and can be filled only like any other workgroup. Empty, it resolves to nobody, so a level holds; it never approves.
- **Catalog group holders** now name a workgroup by its key (DL-068b, changing DL-066a). The catalog checks the key's shape (domain and a CHECK) and that the workgroup exists, through the read-only `IWorkgroupDirectory` contract; workgroups are never deleted, so the reference stays valid.
- **Permissions**: `organization.workgroups.read`, `.manage`, `.approve` (manage and approve conflict), held by the Application administrator and Application checker roles (DL-068d); Auditor reads.
- **API**: `/api/organization/workgroups` (`GET {key}`, `POST`, `PUT {id}/name`, `PUT {id}/draft`, `POST {id}/drafts`, `POST {id}/change-requests`) and `/api/organization/workgroup-change-requests` (`GET {id}`, `approve` with the reviewed hash, `reject`, `withdraw`), with the catalog's protections (unknown fields refused, reasons, bounds, 64 KB body limit, write rate limit, ProblemDetails with codes only).

## Security and privacy impact
- Database guards (`Workgroups` migrations, both engines): a member list becomes active only for an approved request with the same workgroup, version, number and hash, decided by someone not in the list; active and superseded lists and their members never change, except an active list being superseded; decided requests never change; nothing is deleted; PostgreSQL refuses TRUNCATE. SQL Server TRUNCATE fires no trigger (R-035).
- Every change, decision and refusal is audited, with the member list as a sorted snapshot.
- Remaining risk (R-035): member ids are free text checked for shape (as owner ids, DL-067b); the Organization app login can write a fully matching forged approved request by SQL (as R-033, R-034); nothing resolves workgroup members into approvals until slice 2.

## SQL Server vs PostgreSQL impact
Same pattern as ADR-0025: AFTER triggers with `READCOMMITTEDLOCK` on SQL Server, BEFORE triggers with `FOR SHARE`, `ENABLE ALWAYS` and truncate guards on PostgreSQL. Same tests on both (`WorkgroupParityTests`, `WorkgroupApiParityTests`).

## Hybrid deployment impact
None.

## Operational consequences and rollback
New `organization` schema and runtime login (`iam_app_organization`); the API needs `ConnectionStrings:organization`. Someone must propose and approve the first members of `iam_managers`. Down migrations drop the workgroups and their history; roll forward instead once data matters (DL-020).

## Tests / evidence
`Organization/WorkgroupDomainTests`, `Parity/WorkgroupParityTests`, `Parity/WorkgroupApiParityTests`, the catalog tests for group holders, `EndpointPermissionTests` (10 rows). Results in the PR.

## Open questions and approvals
- Workgroup quorum and delegation (IAM-WF-011) belong to the level that uses a workgroup: slice 2.
- Directory groups from connectors as a separate source: later.
- Independent security review and human sign-off.
