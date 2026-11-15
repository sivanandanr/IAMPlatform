# ADR-0006: Transactional outbox / inbox messaging
Status: Accepted and built (outbox, relay and inbox, stage 6, DL-076 to DL-078); pending independent review | Date: 2026-11-07 | Decision owners: Lead Solution Architect, Database Architect (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 006), §6 (`integration` schema), §10; Spec v3.2 IAM-FR-012, FR-OPS-101. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
At-least-once delivery with idempotent consumers; duplicate messages must not duplicate grants (blueprint §16). The outbox must commit in the same transaction as request/approval/provisioning state.

## Options considered
1. Outbox written in the business transaction; relay publishes after commit; consumers dedupe via inbox (proposal). Library choice in ADR-0021.
2. Publish directly to the broker after commit (dual-write risk).
3. Distributed transactions.

## Decision and rationale
Option 1. Messages carry correlation ID, causation ID, idempotency key, schema version and (for provisioning) request-item ID, target identity, operation ID and policy version (blueprint §10). Inbox unique key (ConsumerName, MessageId). Bounded exponential backoff; permanent errors → DLQ / manual repair.

**Envelope and outbox — built (stage 6 slice 1, DL-076).** `IAM.BuildingBlocks.Messaging.MessageEnvelope`: UUIDv7 message id, dotted lower-case type, schema version, source module, correlation and causation ids, an idempotency key fixed by the cause (unique in the outbox), UTC time and a JSON payload of at most 65,536 characters; every field is checked on write and read, reading refuses another type or version and unknown members. Events implement `IIntegrationEvent` (Abstractions) in the publishing module's Contracts and carry ids, codes and sign-in ids only. Delivery is at least once and in no promised order: consumers deduplicate on the message id and order by the event's own version. The Workflow module writes `started`, `task_opened` (only people newly able to act), `authorized` and `closed` in each action's transaction; refusals, timeouts that change nothing and lost races write nothing. A consumer that acts on `authorized` must first verify the instance against the audit chain (R-037 release gate, DL-076a).

**Relay — built (stage 6 slice 2, DL-077).** Each module's worker sends that module's outbox with its own login: claim (attempt counted, lease, saved under the row version), publish to `iam.{module}` with publisher confirms, mark dispatched. Failures back off 2, 4, 8 … seconds up to 15 minutes and park after `MaxAttempts`; unrouted messages are kept by the `iam.unrouted` alternate exchange. Delivery is at least once.

**Inbox and consumers — built (stage 6 slice 3, DL-078).** Each consumer records a processed message in its module's append-only `InboxMessage` (consumer, message id) in the same transaction as its effects, so a duplicate delivery changes nothing (P0-010). Deliveries are read back into envelopes and refused unless they come from the source module's exchange and name that source; unprocessable messages go to the consumer's dead-letter queue; other failures retry after a delay through the consumer's own retry queue, counted by the consumer (RabbitMQ 4 doesn't count requeues), and dead-letter at the limit. Each module has its own broker user, limited by name patterns to its own exchanges and queues.

**Review fixes (DL-079).** Topology comes from the reviewed `infra/rabbitmq/definitions.json` and module users can't configure anything (`infra/rabbitmq/permissions.json`, with topic permissions); each consumer has its own dead-letter exchange; dead-lettering is at-least-once; `iam.unrouted` is capped and never replayed; the inbox also deduplicates on the source's idempotency key; consumers reconnect rather than stall; `closed` and `authorized` may not be consumed until the audit-chain verifier exists.

**Location of outbox/inbox tables — decided (owner, 2026-11-08, DL-021): per-module.** Each module owns its outbox and inbox tables in its own schema, written in the same transaction as its data by its own login. This keeps "no cross-module writes" enforceable in the database (S1-06 per-module logins). Blueprint §6's shared `integration` schema is not used for them. Built in P0-010.

## Security and privacy impact
Payloads contain no tokens, secrets or full HR records (schema assertion). Unknown schema version / oversized / malformed messages go to DLQ unprocessed. Broker users per vhost; no `guest`.

## SQL Server vs PostgreSQL impact
Same-transaction outbox requires module contexts sharing the connection/transaction; tested on both engines. Each module's `OutboxMessage` table is guarded by triggers: no delete, what a message says (type, version, ids, key, time, payload, creation) never changes, a dispatched message stays dispatched, attempts never go down; PostgreSQL also refuses TRUNCATE (`WorkflowOutboxParityTests`).

## Hybrid deployment impact
Broker on private network only; never internet-exposed.

## Operational consequences and rollback
DLQ needs monitoring and a repair runbook.

## Tests / evidence
Rollback leaves no outbox row; duplicate relay → single effect; poison message → DLQ; forged producer rejected. Evidence (stage 6): `WorkflowOutboxParityTests`, `WorkflowOutboxRelayParityTests`, `NotificationInboxParityTests` (duplicates, dead letters, retries, broker permissions, end to end) on both engines, `MessageEnvelopeTests`, `RabbitMqDeliveryTests`.

## Open questions and approvals
- Table location (DL-021); messaging library and licence (ADR-0021).
