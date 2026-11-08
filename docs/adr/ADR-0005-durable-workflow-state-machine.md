# ADR-0005: Durable approval workflow state machine
Status: Proposed | Date: 2026-11-07 | Decision owners: IAM Domain / Workflow lead, Security Architect (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 005), §9, §10; Spec v3.2 Module 7, IAM-WF-001, -003, -004, -006, -009, -011, -012, K.1, K.6; CLAUDE.md rule 3. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Multi-stage approvals (sequential / parallel, quorum ALL / ANY-ONE / N), escalation and replay-safe decisions. No self-approval; mandatory stages cannot be bypassed; no approver, ambiguity or timeout must never grant.

## Options considered
1. Application-owned state machine persisted in the Workflow module's tables with optimistic concurrency (proposal).
2. External workflow engine (e.g. Elsa, Temporal, Camunda).
3. Saga state machine inside the messaging library.

## Decision and rationale
Option 1 for Phase 0 (small, auditable, no new runtime dependency).
- **ApprovalInstance**: Created → Bound (policy version, matched rule, resolved approvers, snapshot) → InProgress → Approved | Rejected | Cancelled | Withdrawn. Held/Escalated reachable from Bound/InProgress; returns to InProgress only when a valid route resolves. Only Approved emits "provisioning authorized" via outbox.
- **StageInstance**: Pending → Active → Approved | Rejected | Escalated | Cancelled. NotApplicable only when a stage condition evaluated false at bind time (recorded); mandatory stages can never be NotApplicable via override (IAM-WF-010).
- **Decision**: Approve / Reject / Complete with actor, delegate-for, reason, timestamp and expected stage version; optimistic concurrency; idempotent (duplicate/replay rejected); quorum evaluated transactionally; ALL/N need distinct eligible identities; one Reject on a required stage rejects the item.
- Self-approval: requester, beneficiary or policy maker removed from candidates; if none remain → "no approver" → hold/escalate.
- Timeout: escalate, reject or cancel — never approve.

## Security and privacy impact
Server-side approver eligibility and independence checks; decisions audited with policy version/hash (IAM-WF-009).

## SQL Server vs PostgreSQL impact
Concurrency via application `Version` column (ADR-0017) for identical semantics; deadlock/serialization retry codes differ (1205/3960 vs 40001/40P01) — retry only idempotent units.

## Hybrid deployment impact
None.

## Operational consequences and rollback
Instances are bound to immutable policy versions; reevaluation only on controlled triggers (IAM-WF-012).

## Tests / evidence
Sequential + parallel ALL + workgroup ANY-ONE; self-approval blocked with alternate resolved; concurrent decisions → exactly one outcome; timeout never approves; mandatory stage cannot be removed. Planned for Sprint 2 (P0-009). Evidence: none yet.

## Open questions and approvals
- Module 7.3 timeout actions "Next level" and "Auto-approve (non-access only)": proposal is to disallow both for mandatory and access-granting stages (DL-013).
- Quorum, duplicate approver across stages, escalation SLA, reevaluation triggers (K.7, L.6 — DL-010).
