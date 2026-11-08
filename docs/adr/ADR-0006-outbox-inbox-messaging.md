# ADR-0006: Transactional outbox / inbox messaging
Status: Proposed | Date: 2026-11-07 | Decision owners: Lead Solution Architect, Database Architect (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 006), §6 (`integration` schema), §10; Spec v3.2 IAM-FR-012, FR-OPS-101. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
At-least-once delivery with idempotent consumers; duplicate messages must not duplicate grants (blueprint §16). The outbox must commit in the same transaction as request/approval/provisioning state.

## Options considered
1. Outbox written in the business transaction; relay publishes after commit; consumers dedupe via inbox (proposal). Library choice in ADR-0021.
2. Publish directly to the broker after commit (dual-write risk).
3. Distributed transactions.

## Decision and rationale
Option 1. Messages carry correlation ID, causation ID, idempotency key, schema version and (for provisioning) request-item ID, target identity, operation ID and policy version (blueprint §10). Inbox unique key (ConsumerName, MessageId). Bounded exponential backoff; permanent errors → DLQ / manual repair.

**Location of outbox/inbox tables — decided (owner, 2026-11-08, DL-021): per-module.** Each module owns its outbox and inbox tables in its own schema, written in the same transaction as its data by its own login. This keeps "no cross-module writes" enforceable in the database (S1-06 per-module logins). Blueprint §6's shared `integration` schema is not used for them. Built in P0-010.

## Security and privacy impact
Payloads contain no tokens, secrets or full HR records (schema assertion). Unknown schema version / oversized / malformed messages go to DLQ unprocessed. Broker users per vhost; no `guest`.

## SQL Server vs PostgreSQL impact
Same-transaction outbox requires module contexts sharing the connection/transaction; tested on both engines.

## Hybrid deployment impact
Broker on private network only; never internet-exposed.

## Operational consequences and rollback
DLQ needs monitoring and a repair runbook.

## Tests / evidence
Rollback leaves no outbox row; duplicate relay → single effect; poison message → DLQ; forged producer rejected. Planned P0-010 (Sprint 2). Evidence: none yet.

## Open questions and approvals
- Table location (DL-021); messaging library and licence (ADR-0021).
