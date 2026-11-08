# ADR-0007: Hybrid private connector workers
Status: Proposed | Date: 2026-11-07 | Decision owners: Lead Solution Architect, Security Architect, Network/Infrastructure owner (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 007), §4, §10; Spec v3.2 Module 2.1, §12.11; CLAUDE.md "Hybrid private connectors". Drafted by Claude Code (S1-01); not approved.

## Context and requirements
AD/LDAP, SAP RFC, databases and the broker must never be exposed to the internet. Connectors run close to target systems, inside customer networks (spec §12.11).

## Options considered
1. Connector worker agents in on-prem private segments, outbound-only mTLS to the platform where feasible (proposal).
2. Central cloud workers reaching on-prem targets over VPN/ExpressRoute only.
3. Expose target systems via reverse proxies.

## Decision and rationale
Option 1, with option 2 acceptable for targets already reachable privately. Connector SDK contract: capabilities, read-before-write, create/update/disable/delete, entitlement add/remove, verification, rate limits, health, reconciliation (blueprint §10). Phase 0 delivers SDK + **mock** AD connector only, clearly labelled as not production-ready (CLAUDE.md rule 10).

## Security and privacy impact
Allowlisted destinations, SSRF controls on configurable endpoints, timeouts, bounded payloads, secret references only, least-privilege service accounts, mTLS/workload identity.

## SQL Server vs PostgreSQL impact
None.

## Hybrid deployment impact
Core of this ADR; traffic matrix to be produced in P0-017.

## Operational consequences and rollback
Agents need lifecycle management (certificate rotation, version skew, health reporting).

## Tests / evidence
Mock connector contract and idempotency tests (P0-011, Sprint 2). Evidence: none yet.

## Open questions and approvals
- Network topology and data residency approval (blueprint §17).
