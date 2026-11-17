# ADR-0009: Immutable published policy versions with maker-checker
Status: Proposed; the approval matrix is built on it (stage 8 slice 3, DL-090) | Date: 2026-11-07 | Decision owners: Security Architect, IAM Domain / Workflow lead, Business policy owners (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 009); Spec v3.2 DEC-003, IAM-FR-003, IAM-WF-007, -008, -019, FR-POL-101/103; CLAUDE.md rule 4. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Sensitive provisioning policy, approval matrix, workflow and authorization-affecting attribute changes require independent maker-checker and immutable published versions; pending/in-flight work keeps its evaluated version.

## Options considered
1. Draft → PendingApproval → Published (immutable) → Superseded/Retired; rollback = republish a previous version as a new version (proposal).
2. Editable live configuration with change log.

## Decision and rationale
Option 1. Checker ≠ maker, enforced server-side (identity comparison, not UI). Published versions carry content hash; evaluation evidence references version + hash. Pre-publication validation rejects ambiguous equal-precedence rules (FR-POL-102). Simulation before publication is required by IAM-WF-008 (P0) / IAM-FR-001G; bulk impact analysis is IAM-WF-021 (P1) — scope for Phase 0 needs confirmation (DL-014).

## The stored approval matrix (stage 8 slice 3, DL-090)
One enterprise matrix per deployment, schema `policy`. A version is a canonical JSON document of rules, each a typed condition (ADR-0004) and the route it selects: ordered stages with an approver source (entitlement owner, application owner, application role, workgroup), quorum, mandatory and independent flags, a deadline, a timeout action that is never approve, and Decision or Action kind (Action stages list the name fields they may fill). Every route has a Decision stage. A draft is compiled on every save against the published catalog and the built-in fact attributes; possible same-priority overlaps block proposal and approval. Each version stores its content hash and the rules engine's compiled hash (attribute versions included); a second person who neither proposed nor wrote it activates it, and approval recompiles it so an attribute changed since is refused. The selector (`IApprovalMatrix`) recompiles the active version and holds if it no longer gives the approved hash; every outcome but a match is a hold. Database triggers refuse activation without an approved request naming the exact content and compiled hash, by its author, and any change to active versions or decided requests. HTTP: `GET /api/policy/matrix`, `GET /api/policy/matrix/facts`, `PUT /api/policy/matrix/draft`, `POST /api/policy/matrix/change-requests`, `GET|POST /api/policy/matrix-change-requests/{id}[/approve|reject|withdraw]`.

## Security and privacy impact
Prevents single-actor policy changes; audit captures maker, checker, before/after.

## SQL Server vs PostgreSQL impact
Immutability enforced by application and by denying UPDATE on published-version rows to runtime identity where practical; parity-tested.

## Hybrid deployment impact
None.

## Operational consequences and rollback
Rollback is a new published version; no in-place edit.

## Tests / evidence
Maker cannot approve own change; published version update rejected; ambiguous rules fail publication. Evidence: none yet.

## Open questions and approvals
- Does maker-checker apply to AttributeDefinition activation in Sprint 1, or stay locked until built (DL-007)?
- Simulation scope in Phase 0 (DL-014).
