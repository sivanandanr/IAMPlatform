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

## Open questions and approvals
- Tenancy scope of authorization (ADR-0014).
- Maker-checker for role/permission changes in Phase 0 (DL-016).
- Break-glass design ADR (later).
