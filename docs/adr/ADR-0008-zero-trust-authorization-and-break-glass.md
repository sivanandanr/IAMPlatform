# ADR-0008: Zero-trust server-side authorization and independent break-glass
Status: Proposed | Date: 2026-11-07 | Decision owners: Security Architect, Lead Solution Architect (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 008), §8; Spec v3.2 IAM-FR-004, IAM-FR-023, FR-SEC-101/102, §12.2, §12.3, "How the product decides whether an action can run"; CLAUDE.md rules 3, 6 and secure engineering standards. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Authorization is enforced server-side per action and resource; frontend checks are UX only. Break-glass must be a separate controlled path usable during IdP/workflow outage, with alerting and independent post-use review (DEC-004).

## Options considered
1. Permission-based authorization keyed to an action catalog; roles are data; default deny (proposal).
2. Hard-coded role names on endpoints.
3. IdP group claims as permissions.

## Decision and rationale
Option 1.
- Fallback authorization policy requires an authenticated user; only liveness and the OIDC callback are anonymous (reviewed allowlist).
- Endpoints require permissions mapped to action-catalog IDs; never role names.
- Permissions resolved server-side from the current store each request (revocation takes effect immediately), not from client or IdP claims alone.
- Separated duties seeded as distinct roles: platform administrator, policy author, policy checker, approver, auditor, connector operator, break-glass custodian. Incompatible pairs (author+checker; custodian+any) rejected.
- **Break-glass is out of Sprint 1.** No hidden local-admin or bypass path may be added to the skeleton. A dedicated ADR will cover it before implementation.

## Security and privacy impact
Core security control. Object-level checks on every resource (BOLA). Authorization denials audited.

## SQL Server vs PostgreSQL impact
Permission/role tables in Administration/Identity module schema; parity tests apply.

## Hybrid deployment impact
Break-glass must work during IdP outage — implies a path not dependent on the primary IdP; design deferred.

## Operational consequences and rollback
Role/permission changes may themselves need maker-checker (spec §12.2 — DL-016).

## Tests / evidence
Endpoint enumeration test vs anonymous allowlist; 401 unauthenticated; 403 without permission; conflicting role pair → 422; revoke then next request denied. Evidence: none yet.

## Stage 2 storage (P0-013, DL-016, DL-059): slices 1 and 2
Drafted by Claude Code; not approved. Nothing here is reachable from the API yet (slice 3), so R-023 stays open.
- **Model.** Action ids are constants in code (`ActionCatalog`); roles, their actions, assignments and incompatible pairs are data (`AccessModel`). Default deny: an unknown action or person can do nothing. Six default roles; IAM Administrator proposes, IAM Manager approves (DL-059l); Administrator with Manager, Administrator with Auditor and Author with Checker are incompatible. The break-glass custodian role exists but can't be assigned (break-glass needs its own ADR).
- **Second person (DL-016).** Every change is a change request: one person proposes, a different person who can approve decides. Nobody can propose or approve a change that grants themselves a role or changes a role they hold, and the checks run again at approval against the model as it is then. A change that would leave nobody able to propose, or nobody else able to approve, is refused.
- **Bootstrap (DL-059e).** Once only, with two different people, and refused when anything is stored. The operator command that calls it is slice 5.
- **Storage.** Schema `configuration`: `AccessRole`, `AccessRoleAction`, `AccessRoleAssignment`, `AccessIncompatibleRolePair`, append-only `AccessModelVersion`, and `AccessChangeRequest` (the change as a kind plus strict JSON). The model is read from the database on every call, between two reads of the latest version, so a torn read is retried and never returned (DL-059f).
- **Concurrency.** Every applied change inserts the next model version number in the same transaction. The primary key lets only one of two concurrent approvals apply; the other gets a conflict and stores nothing. The change row's row version stops two decisions on the same request.
- **Audit.** Bootstrap, propose, approve, reject and withdraw are audited in the same transaction (ADR-0018). Refusals are audited as denied with the refusal code only.
- **Database guarantees.** Triggers on both engines: model versions can't be updated, deleted or (PostgreSQL) truncated; what was proposed, by whom and why never changes; decided requests never change; no request is deleted. CHECKs repeat the state rules (for example an approval names a checker who is not the maker), allow only known kinds, and require valid JSON. The schema owner is bound by the triggers but can still drop them (as R-026).
- **Review fixes (DL-060).** Separation of duties is checked on actions (no role or person may both propose and approve); a revoked maker's proposal can't be approved; each version stores a hash of the whole model and is linked to its approved request both ways (foreign keys, which also block TRUNCATE on SQL Server, and a trigger that lets only a still-proposed request claim a version); every read checks the hash, and a damaged or edited store raises `AccessStoreIntegrityException`, audited as `administration.access.integrity_failed`. Approval claims the next version before writing anything else and reads the model before the request. Subjects are limited to 200 characters, like every stored actor. Decisions record the model version and hash they were made against (`PolicyVersionRef`, `PolicyVersionHash`). Not-found requests and conflicts are not audited (nothing was decided). SQL Server can't check duplicate JSON keys; the codec refuses them on read.
- **Re-review fixes (DL-061).** Conflicting duties are a code list (propose/approve, propose/escalate). Stored hashes are `v1:` + hex; the read also checks that the latest version names a request approved as it. The integrity audit is written without the caller's cancellation and can't hide the failure it records (`AuditFailure`); it records which check failed. A claim refused because the request was just decided is a conflict; other claim failures get a neutral message. The claim trigger holds the request row until commit (PostgreSQL `FOR NO KEY UPDATE`, SQL Server `UPDLOCK`). Repair: `docs/runbooks/ACCESS_CONTROL_INTEGRITY.md`. The hash is not keyed (R-033, P0-013b).
- **Evidence.** Domain tests and generated invariant tests (Administration.Tests), codec tests, and `AccessControlParityTests` run locally on PostgreSQL and SQL Server (84 of 84 parity tests on each after the re-review fixes, 2026-11-13; on SQL Server a single-NULL unique index refuses stray bootstrap or unlinked version rows before the CHECK does). The first SQL Server run found two CHECK clauses that let NULLs through (a CHECK that evaluates to UNKNOWN passes); both now test IS NOT NULL explicitly, with a test row for each.

## Stage 2 slice 3: permission checks on every endpoint (DL-062)
Drafted by Claude Code; not approved.
- Endpoints declare the action they need with `RequirePermission("<action id>")` (`IAM.BuildingBlocks.Security`). It adds a requirement on top of the default authenticated policy, so no token is still 401 and a denied action is 403.
- The handler takes the caller's `sub` from the validated token (never a claim of roles) and asks `IPermissionEvaluator`. The Administration module answers from the stored model, read once per request and never cached across requests, so revocation applies on the next request. Unknown actions and empty subjects are refused. Actions everyone signed in has are answered without reading the store.
- A damaged store (`AccessStoreIntegrityException`) is a 503 ProblemDetails with no details and a logged error: never a grant. Access failures map to ProblemDetails with at most a refusal code: 403 who you are, 409 wrong state or conflict, 422 invalid change, 404 not found.
- An API test enumerates every mapped endpoint and fails if one names no action, names an unknown action, or is anonymous without being on the reviewed list (today only `/health/live`). Action ids other modules use are in `Administration.Contracts.AccessActions`, kept identical to `ActionCatalog` by a test.
- First endpoint: `GET /api/administration/permissions/me` returns the caller's own role codes and action ids only.

## Stage 2 slices 4-5: admin API and first setup (DL-062)
Drafted by Claude Code; not approved.
- `/api/administration`: `GET /roles` (`roles.read`; roles and what they allow, not who holds them), `GET /changes/{id}` (`changes.read`), `POST /changes` (`changes.propose`), `POST /changes/{id}/approve` and `/reject` (`changes.approve`), `/withdraw` (`changes.propose`, maker only).
- Bodies are narrow DTOs that refuse unknown properties (mass assignment), checked by FluentValidation 12.2.0 through an endpoint filter on every write (only the chosen kind's fields; codes, lengths, counts). The server generates change ids; the maker and checker come from the token. The domain checks every rule again.
- Host JSON: depth 32, duplicate keys refused, HTML-significant characters escaped in responses. Writes are limited to 16 KB (413) and 30 per person per minute (429, configurable).
- First setup is `IAM.Tools.AccessBootstrap`, an operator command with no HTTP surface (runbook `ACCESS_CONTROL_BOOTSTRAP.md`), composed through module Endpoints like a host; the architecture rules now cover tools.
- Review fixes (DL-063): denials at the permission check are audited (`administration.authorization.denied`, throttled per person and action); the limiter runs before authorization and covers every request (per person, or per address without a token); request body limits are enforced while the body is read (`RequireBodyLimit`), chunked bodies included; security headers are set when the response starts, so error responses carry them; a last-resort handler returns a plain 500 and logs the exception type only; the default authorization policy is pinned to the JWT scheme. The bootstrap tool needs the dedicated `iam_bootstrap` login (`grant-access-bootstrap.sql` / `revoke-access-bootstrap.sql`) and an approval reference, and never crashes or prints exception messages.
- Accepted: refusal codes such as `AlreadyAssigned` tell a proposer whether someone holds a role (DL-063d). `POST /changes` has no idempotency key, so a retried call can create a second pending request (it can be withdrawn); approvals are safe to replay (409).
- Evidence: `AccessApiParityTests` and `AccessBootstrapToolParityTests` run the real host on both engines (mandatory negative list of BACKEND_INPUT_VALIDATION); validator unit tests.

## Open questions and approvals
- Tenancy scope of authorization (ADR-0014).
- Maker-checker for role/permission changes in Phase 0 (DL-016).
- Break-glass design ADR (later).
