# ADR-0011: Module list and naming
Status: Accepted by repository owner (sivanandanr, 2026-11-07, via Claude Code session) — independent Lead Solution Architect review pending | Date: 2026-11-07 | Decision owners: Lead Solution Architect, Functional Lead / Product owner

Source: CLAUDE.md "Stack and boundaries"; Blueprint v1.0 §4, §5, §6. Decision log DL-002. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
The authoritative sources disagree:

| Source | Modules |
|---|---|
| CLAUDE.md | Identity, Organization, Catalog, Account, Policy, Workflow, Request, Provisioning, Governance, Audit, Integration, Notification, Administration (13) |
| Blueprint §5 (repo layout) | Identity, Catalog, Lifecycle, Policy, Workflow, AccessRequest, Governance, Audit, Administration (9) |
| Blueprint §4 (logical) | Identity, Catalog, Lifecycle, Policy, Workflow, Request, Governance, Audit, Administration |
| Blueprint §6 (schemas) | identity, organization, catalog, account, policy, workflow, request, provisioning, governance, audit, integration, notification, configuration (no `lifecycle`) |

## Options considered
1. CLAUDE.md list (13); `configuration` schema owned by Administration; Lifecycle (JML orchestration) only if the Functional Lead confirms it is a separate module, otherwise JML orchestration lives in Identity + Request.
2. Blueprint §5 list (9), with Organization/Account/Provisioning/Integration/Notification folded into others or treated as workers only.
3. Merged: CLAUDE.md list + Lifecycle (14).

## Decision and rationale
**Decided (repository owner, 2026-11-07): option 3 — CLAUDE.md 13 modules + Lifecycle (14 modules).**

Identity, Organization, Catalog, Account, Policy, Workflow, Request, Provisioning, Governance, Audit, Integration, Notification, Administration, **Lifecycle**.
- Naming: "Request" (not "AccessRequest").
- `configuration` schema owned by Administration.
- Lifecycle owns joiner/mover/leaver orchestration (access impact plans, FR-BR-101/104); it consumes Identity and Policy through Contracts and does not write their tables. Its schema `lifecycle` is new relative to blueprint §6.

Reviewers had recommended option 1; the owner chose to keep Lifecycle as a separate module.

## Security and privacy impact
Module = schema = DB identity boundary (ADR-0017); fewer, larger modules widen each identity's write scope.

## SQL Server vs PostgreSQL impact
One schema and migration history per module on both engines; more modules = more migration sets to review.

## Hybrid deployment impact
Provisioning, Integration and Notification modules back worker processes; their placement follows ADR-0007.

## Operational consequences and rollback
Renaming/merging modules after S1-02 is costly; decide before scaffolding.

## Tests / evidence
Architecture tests enforce the approved list. Evidence: none yet.

## Open questions and approvals
- Is Provisioning/Notification/Integration a module (with API + schema) or only a worker?
- Is Lifecycle a module?
