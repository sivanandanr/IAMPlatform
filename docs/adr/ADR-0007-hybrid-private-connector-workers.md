# ADR-0007: Hybrid private connector workers
Status: Accepted in part (connector SDK, mock AD connector, speed limits and reconciliation, stage 7, DL-080 to DL-083); reviewed (stage 7 security and code reviews, fixes DL-083); pending human approval | Date: 2026-11-07 | Decision owners: Lead Solution Architect, Security Architect, Network/Infrastructure owner (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 007), §4, §10; Spec v3.2 Module 2.1, §12.11; CLAUDE.md "Hybrid private connectors". Drafted by Claude Code (S1-01); not approved.

## Context and requirements
AD/LDAP, SAP RFC, databases and the broker must never be exposed to the internet. Connectors run close to target systems, inside customer networks (spec §12.11).

## Options considered
1. Connector worker agents in on-prem private segments, outbound-only mTLS to the platform where feasible (proposal).
2. Central cloud workers reaching on-prem targets over VPN/ExpressRoute only.
3. Expose target systems via reverse proxies.

## Decision and rationale
Option 1, with option 2 acceptable for targets already reachable privately. Connector SDK contract: capabilities, read-before-write, create/update/disable/delete, entitlement add/remove, verification, rate limits, health, reconciliation (blueprint §10). Phase 0 delivers SDK + **mock** AD connector only, clearly labelled as not production-ready (CLAUDE.md rule 10).

**SDK — built (stage 7 slice 1, DL-080).** `IAM.Connectors.Sdk`: the action catalog (the spec's 15 actions as stored codes), capabilities, the operation and result model, and `ConnectorBase`, a shared pipeline that validates every operation and makes it safe to repeat (operation-id ledger, read-before-write, one operation per target at a time). Credentials travel only as vault references; destructive actions start off. `ConnectorContractTests` is the suite every connector must pass. Connectors may not reference modules or persistence (architecture rule).

**Mock AD connector — built (stage 7 slice 2, DL-081). Not production-ready.** `IAM.Connectors.ActiveDirectory.Mock` covers the whole catalog over an in-memory directory with strict distinguished-name rules, two-sided group membership, and credential actions that never hold a secret. It passes the contract suite and says it is a mock in its capabilities and health.

**Speed limits, faults and reconciliation — built (stage 7 slice 3, DL-082).** The pipeline enforces the lower of the connector's and the application's speed limits; `Reconciler` marks objects missing only after health, pagination, scope and mass-removal checks all pass; the mock simulates latency, failures, an unavailable directory, failed listings and drift.

**Review fixes — built (DL-083).** A create never takes over an existing object; identifiers compare the connector's way (the mock AD ignores case); privilege and credential attributes are on a fixed denylist and cut from anything a connector returns; password change and reset are off unless switched on and limited to one vault path; a connector's own fault is reported, never thrown; the reconciliation mass-removal guard can't be switched off.

## Security and privacy impact
Allowlisted destinations, SSRF controls on configurable endpoints, timeouts, bounded payloads, secret references only, least-privilege service accounts, mTLS/workload identity.

## SQL Server vs PostgreSQL impact
None.

## Hybrid deployment impact
Core of this ADR; traffic matrix to be produced in P0-017.

## Operational consequences and rollback
Agents need lifecycle management (certificate rotation, version skew, health reporting).

## Tests / evidence
Mock connector contract and idempotency tests (P0-011). Slice 1: `IAM.Connectors.Tests` (`SdkTests`, the contract suite against a minimal test connector), architecture rule `ConnectorsKnowNothingOfModulesOrDatabases`.

## Open questions and approvals
- Network topology and data residency approval (blueprint §17).
