# ADR-0021: Messaging library
Status: Accepted (owner decision, 2026-11-15, DL-076) — option 4 | Date: 2026-11-07 | Decision owners: Lead Solution Architect; licence approval by procurement/legal (named approvers TBD)

Source: CLAUDE.md stack ("MassTransit/RabbitMQ with outbox/inbox"); Blueprint v1.0 §3, §10; ADR-0006. Decision log DL-018. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Durable queues, outbox/inbox, DLQ, bounded retries over RabbitMQ.

## Options considered
1. MassTransit v8 (Apache-2.0; reviewers understand its support window is limited — **verify end-of-support date**).
2. MassTransit v9 (commercial licence).
3. Wolverine (verify licence).
4. RabbitMQ.Client + custom outbox/inbox relay.

## Decision and rationale
**Option 4: RabbitMQ.Client with an in-house outbox, relay and inbox** (owner, 2026-11-15, DL-076). MassTransit v8 (Apache-2.0) gets official maintenance only until about the end of 2026 and v9 needs a commercial licence ([announcement](https://masstransit.io/introduction/v9-announcement)); the in-house option has no licence cost or end-of-support risk, at the price of code we own and test. The owner approved changing CLAUDE.md's stack line accordingly. RabbitMQ.Client (Apache-2.0/MPL-2.0) is referenced only by the relay and consumer hosts (slice 2 onward); the `IAM.BuildingBlocks.Messaging` envelope has no packages, so Application layers may use it. Testcontainers.RabbitMq (MIT) is approved for broker tests.

## Security and privacy impact
Whatever library: per-vhost broker users, no `guest`, TLS, bounded payloads, schema-versioned messages, DLQ for poison messages.

## SQL Server vs PostgreSQL impact
Library outbox storage must support both engines (verify provider support for the chosen option).

## Hybrid deployment impact
Broker private-network only.

## Operational consequences and rollback
Library swap later touches the Messaging building block only if modules depend on abstractions.

## Tests / evidence
ADR-0006 tests. Slice 1: `MessageEnvelopeTests`, `WorkflowOutboxParityTests`, architecture rule `MessagingStaysFreeOfPackagesAndOtherBuildingBlocks`.

## Open questions and approvals
- Licence/support verification and budget decision.
