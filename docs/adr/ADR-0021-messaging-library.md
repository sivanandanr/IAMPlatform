# ADR-0021: Messaging library
Status: Proposed — blocking (P0-010, Sprint 2; outbox tables in S1-06) | Date: 2026-11-07 | Decision owners: Lead Solution Architect; licence approval by procurement/legal (named approvers TBD)

Source: CLAUDE.md stack ("MassTransit/RabbitMQ with outbox/inbox"); Blueprint v1.0 §3, §10; ADR-0006. Decision log DL-018. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Durable queues, outbox/inbox, DLQ, bounded retries over RabbitMQ.

## Options considered
1. MassTransit v8 (Apache-2.0; reviewers understand its support window is limited — **verify end-of-support date**).
2. MassTransit v9 (commercial licence).
3. Wolverine (verify licence).
4. RabbitMQ.Client + custom outbox/inbox relay.

## Decision and rationale
**Not decided.** Trade-off: v8 risks an unsupported dependency during Phase 1; v9 needs budget; custom relay costs engineering effort but has no licence exposure. CLAUDE.md names MassTransit, so options 3/4 would also require updating CLAUDE.md through approved change.

## Security and privacy impact
Whatever library: per-vhost broker users, no `guest`, TLS, bounded payloads, schema-versioned messages, DLQ for poison messages.

## SQL Server vs PostgreSQL impact
Library outbox storage must support both engines (verify provider support for the chosen option).

## Hybrid deployment impact
Broker private-network only.

## Operational consequences and rollback
Library swap later touches the Messaging building block only if modules depend on abstractions.

## Tests / evidence
ADR-0006 tests. Evidence: none yet.

## Open questions and approvals
- Licence/support verification and budget decision.
