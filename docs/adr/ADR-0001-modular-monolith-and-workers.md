# ADR-0001: Modular monolith with independently deployable workers
Status: Proposed | Date: 2026-11-07 | Decision owners: Lead Solution Architect, Security Architect (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 001), §4, §5; CLAUDE.md "Stack and boundaries". Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Phase 0 needs low operational complexity with explicit boundaries that can be extracted later on evidence. CLAUDE.md forbids premature microservices and cross-module table writes. Background work (provisioning, discovery, HR sync, notifications, governance jobs) has different scaling, network placement and failure characteristics from the interactive API.

## Options considered
1. Modular monolith API + separately deployable workers (blueprint proposal).
2. Microservices per module from day one.
3. Single process including workers.

## Decision and rationale
Option 1 (proposed). One ASP.NET Core 10 Minimal API host composes all modules; workers are separate executables sharing BuildingBlocks and module Contracts. Each module has Domain / Application / Infrastructure / Contracts / Endpoints. Modules interact only through `Contracts` (in-process queries) or integration events via outbox (ADR-0006). The module list itself is decided in ADR-0011.

## Security and privacy impact
Smaller attack surface than many services; but a single process means a compromised module shares memory/credentials, so per-module DB identities (ADR-0017) and server-side authorization per endpoint (ADR-0008) are required. Workers run with their own least-privilege identities.

## SQL Server vs PostgreSQL impact
None directly; schema-per-module applies equally (ADR-0017).

## Hybrid deployment impact
Connector/provisioning workers can be deployed inside private on-prem segments (ADR-0007) while the API runs in DMZ/cloud.

## Operational consequences and rollback
API and workers are versioned and deployed independently but must be contract-compatible (versioned event schemas). Extraction to a service later = moving a module behind its existing Contracts.

## Tests / evidence
Architecture tests (S1-02): no module references another module's non-Contracts assembly; Workers/Connectors do not reference Endpoints. Evidence: none yet.

## Open questions and approvals
- Module list (ADR-0011).
- Approval by named architecture and security owners.
