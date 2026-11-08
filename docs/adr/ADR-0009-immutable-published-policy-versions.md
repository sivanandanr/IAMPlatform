# ADR-0009: Immutable published policy versions with maker-checker
Status: Proposed | Date: 2026-11-07 | Decision owners: Security Architect, IAM Domain / Workflow lead, Business policy owners (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 009); Spec v3.2 DEC-003, IAM-FR-003, IAM-WF-007, -008, -019, FR-POL-101/103; CLAUDE.md rule 4. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Sensitive provisioning policy, approval matrix, workflow and authorization-affecting attribute changes require independent maker-checker and immutable published versions; pending/in-flight work keeps its evaluated version.

## Options considered
1. Draft → PendingApproval → Published (immutable) → Superseded/Retired; rollback = republish a previous version as a new version (proposal).
2. Editable live configuration with change log.

## Decision and rationale
Option 1. Checker ≠ maker, enforced server-side (identity comparison, not UI). Published versions carry content hash; evaluation evidence references version + hash. Pre-publication validation rejects ambiguous equal-precedence rules (FR-POL-102). Simulation before publication is required by IAM-WF-008 (P0) / IAM-FR-001G; bulk impact analysis is IAM-WF-021 (P1) — scope for Phase 0 needs confirmation (DL-014).

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
