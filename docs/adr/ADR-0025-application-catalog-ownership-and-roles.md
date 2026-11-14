# ADR-0025: Application catalog — owner, application roles and second-person activation
Status: Proposed (decisions taken by the owner as DL-066, 2026-11-14) — stage 3 slice 1 implemented; human sign-off pending | Date: 2026-11-14 | Decision owners: IAM Domain lead, Security Architect (named approvers TBD)

Source: Spec v3.2 Module 2.2 (onboarding: "Submitting needs a second person. The maker can't approve it."), 2.3 (Owner; "There is no business owner field"), IAM-FR-010 (application owner, technical custodian and entitlement owner distinguishable and independently assignable); `requirements.md` AR-2.1 to AR-2.4; DL-051, DL-052, DL-055(h, i), DL-066; CLAUDE.md rules 4, 5 and 11. Drafted by Claude Code.

## Context and requirements
Stage 4 resolves approvers from application roles (AR-2.5, DL-048), so the catalog must first hold, for each application, one required owner and optional application roles (AR-2.1: a catalog table, not fixed columns; AR-2.2: held by a person or a group, Optional or Required; AR-2.3: an empty required role blocks activation and requests). Changing who owns or administers an application changes who will approve access to it, so it is a sensitive change (CLAUDE.md rule 4).

## Options considered
1. Owner and roles as columns on the application, changed in place with audit only.
2. Owner and roles in numbered settings versions, activated by a second person (the attribute publishing pattern, ADR-0015, DL-064/065).
3. A generic change-request table shared with Administration.

## Decision and rationale
Option 2 (DL-066). Reuses the reviewed attribute publishing design and the shared `SecondPersonDecision`; keeps Catalog's data in its own schema with no cross-module writes (option 3 would need one).
- **Application**: `catalog.Application` with an immutable key and a display name. Renaming needs `catalog.applications.manage` and is audited, with no second person (a name is not access, rule 11). Applications are never deleted.
- **Settings versions**: `ApplicationSettingsVersion` (Draft → Active → Superseded) holds the owner (one person, AR-2.4) and `ApplicationSettingsRole` rows (role type, Optional/Required, holder kind Person or Group with an id, or empty). At most one draft and one active version per application. The application is active once it has an active version.
- **Activation**: `ApplicationSettingsRequest` names the draft's version number, content hash and owner. A different person with `catalog.applications.approve` approves; the maker must still hold `manage`; the proposed owner can't decide; whoever created or last edited the draft can't approve; the approver sends the content hash they reviewed; the draft is frozen while the request is pending. Approval activates the draft and supersedes the active version in one transaction with the audit event. The same flow covers first activation and every later owner or role change (DL-066b).
- **Group holders** (DL-066a): stored as an opaque directory group id. Nothing resolves members or trusts the id in stage 3.
- **Role types** (DL-066c): User Admin, IT Admin and Technical custodian are built in (fixed ids, seeded by the migration) and can't be removed or changed. A deployment's own type is proposed and is usable only after a second person approves it; nothing is deleted; a withdrawn or rejected type frees its code.
- **Permissions** (DL-066d): `catalog.applications.read`, `.manage`, `.approve`; manage and approve are conflicting duties; new default roles Application administrator (read, manage) and Application checker (read, approve), an incompatible pair; Auditor reads.

## Security and privacy impact
- Database guards (`ApplicationCatalog` migrations, both engines), so the Catalog app login can't bypass the service: a settings version becomes active only for an approved request with the same application, version, number, content hash and owner; not with an empty required role; not with a role type that isn't built in or approved. Active and superseded versions, their role rows and decided requests never change; requests, applications and role types are never deleted; a new role type row must be Proposed; built-in and decided types never change. CHECKs: state shapes (no self-decision, the proposed owner never decides), hex hashes, key formats, one pending request per application.
- Remaining risk (R-034): the app login can still write a forged approved request whose details all match a draft, or supersede an active version, by SQL (same class as R-033). A draft's role rows can be changed by SQL while a request is pending; the trigger checks the hash column, not the rows. Group ids are not validated against a directory.
- Audit: every change, decision and refusal (`catalog.application.*`, `catalog.role_type.*`), with codes and ids; owner and maker ids are recorded as accountability facts; holder ids are covered by the content hash, not listed.

## SQL Server vs PostgreSQL impact
Hand-written CHECKs, filtered unique indexes and triggers on both engines; SQL Server AFTER triggers with `READCOMMITTEDLOCK`, PostgreSQL BEFORE triggers with `FOR SHARE`, `ENABLE ALWAYS` and truncate guards. Same tests on both (`ApplicationCatalogParityTests`).

## Hybrid deployment impact
None: the catalog is central data; no connector is involved yet.

## Operational consequences and rollback
New `catalog` schema and runtime login (`iam_app_catalog`, bootstrap script as other modules). The Down migrations drop the catalog with its history and are only valid where no catalog data matters; otherwise roll forward (DL-020). Existing deployments must add the new default roles through an access change. The module is not yet wired into the API host (slice 2).

## Tests / evidence
`tests/IAM.Persistence.Tests/Catalog/ApplicationCatalogDomainTests` (keys, settings rules, hash, empty required roles, owner and maker can't decide, edited draft refused, supersede, role types); `Parity/ApplicationCatalogParityTests` on both engines (migration and seed, activation with audit, empty required role, every refusal path with audit, freeze while pending, owner change through a new version, permission checks, rename, role types, SQL refusals). Results are recorded in the PR.

## Open questions and approvals
- Retiring an application, AR-2.6 (empty optional roles at request time) and the "Application role" approver type: stage 4.
- AR-2.8 (succession and ownership reviews of roles): Governance, later.
- Validating group ids against a directory: with the person directory (DL-059d).
- Independent security review and human sign-off.
