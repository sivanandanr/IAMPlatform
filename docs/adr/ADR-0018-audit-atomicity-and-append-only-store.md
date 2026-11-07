# ADR-0018: Audit atomicity and append-only audit store
Status: Proposed — blocking (S1-07) | Date: 2026-11-07 | Decision owners: Security Architect, Database Architect, Compliance owner (named approvers TBD)

Source: Spec v3.2 IAM-FR-026, IAM-FR-027 ("acknowledged audit records are never lost"), IAM-WF-009, -020, §12.6 (sealed into a hash chain every minute), §12.10, OPEN-002; Blueprint v1.0 §6 (`audit` schema), §8; CLAUDE.md rule 9. Decision log DL-022, DL-023. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Audit every sensitive change, decision, policy/version snapshot, correlation ID and connector outcome without leaking secrets or sensitive attributes; tamper-evident.

## Options considered
1. Audit writer from `Audit.Contracts` invoked inside the caller's transaction (same DB, `audit` schema, `iam_audit_writer` permissions).
2. Business transaction writes outbox; Audit consumer persists asynchronously.
3. Dedicated audit outbox per module, relayed to the audit store.

## Decision and rationale
**Not decided.** Recommendation for Phase 0: option 1 (strongest "never lost" guarantee; simplest). Event fields: Id, Seq, OccurredAtUtc, RecordedAtUtc, ActorId, EffectiveActorId, Action, TargetType, TargetId, Reason, Outcome, CorrelationId, CausationId, PolicyVersionRef/Hash, masked PayloadCanonical, PayloadHash, SchemaVersion (+ TenantId per ADR-0014).

Append-only: `DENY UPDATE, DELETE, ALTER ON SCHEMA::audit` (SQL Server) / `REVOKE UPDATE, DELETE, TRUNCATE` (PostgreSQL) for runtime identities; triggers raising errors as a second layer. Masking happens **before** hashing (hashed data cannot be redacted later).

**Hash-chain sealing (spec §12.6) is NOT implemented in Sprint 1** and must be reported as such. Planned design: cluster-singleton sealer under advisory lock writing `audit.EvidenceAnchor` (Seq range, batch digest, previous anchor hash, HMAC/signature with vault key id); must not assume gap-free identity values.

## Security and privacy impact
DBA/superuser can still alter data; SIEM/WORM export is the real immutability control (later). Sensitive values masked.

## SQL Server vs PostgreSQL impact
Grant/trigger syntax differs; optional SQL Server ledger tables are not portable and conflict with retention purge — not proposed. PK includes RecordedAtUtc to allow later PostgreSQL partitioning.

## Hybrid deployment impact
Audit export destinations (SIEM) to be decided.

## Operational consequences and rollback
Retention purge requires a separate approved identity and procedure respecting legal hold — not in Sprint 1.

## Tests / evidence
App identity UPDATE/DELETE on audit fails on both engines; audit row commits/rolls back with the business change; no tokens or sensitive raw values in audit. Evidence: none yet.

## Open questions and approvals
- Retention periods and legal-hold authority (OPEN-002; spec §12.10 gives periods while H says pending) — DL-023.
- K.5 `audit.ApprovalAuditEvent` vs blueprint `audit.AuditEvent` naming.
