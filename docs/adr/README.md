# Architecture Decision Records

ADRs are **Proposed** unless the approval record below says otherwise. None is Accepted until a named human approver records approval and date in the ADR. Agents may draft and recommend; they never mark an ADR Accepted (CLAUDE.md: never self-approve).

Template: [ADR_TEMPLATE.md](ADR_TEMPLATE.md). Open questions are tracked in [../architecture/DECISION_LOG.md](../architecture/DECISION_LOG.md); risks in [../architecture/RISK_REGISTER.md](../architecture/RISK_REGISTER.md).

## Blueprint baseline (Blueprint v1.0 §1, ADR 001–010)
| ADR | Title | Status |
|---|---|---|
| [0001](ADR-0001-modular-monolith-and-workers.md) | Modular monolith with independently deployable workers | Proposed |
| [0002](ADR-0002-one-database-engine-per-deployment.md) | One relational database engine per deployment | Proposed |
| [0003](ADR-0003-provider-specific-migrations.md) | Provider-specific migrations and migration governance | Proposed (recovery approach decided) |
| [0004](ADR-0004-typed-metadata-driven-policy-engine.md) | Typed metadata-driven policy engine | Proposed |
| [0005](ADR-0005-durable-workflow-state-machine.md) | Durable approval workflow state machine | Proposed |
| [0006](ADR-0006-outbox-inbox-messaging.md) | Transactional outbox / inbox messaging | Proposed (table location decided) |
| [0007](ADR-0007-hybrid-private-connector-workers.md) | Hybrid private connector workers | Proposed |
| [0008](ADR-0008-zero-trust-authorization-and-break-glass.md) | Zero-trust authorization and independent break-glass | Proposed |
| [0009](ADR-0009-immutable-published-policy-versions.md) | Immutable published policy versions with maker-checker | Proposed |
| [0010](ADR-0010-cross-provider-ci-matrix.md) | Cross-provider CI parity matrix | Proposed |

## Phase 0 Sprint 1 decisions
| ADR | Title | Status | Blocks |
|---|---|---|---|
| [0011](ADR-0011-module-list-and-naming.md) | Module list and naming | Accepted (owner) | S1-02 |
| [0012](ADR-0012-browser-session-bff.md) | Browser session model — BFF | Accepted (owner) | S1-05 |
| [0013](ADR-0013-yarp-gateway-placement.md) | YARP gateway placement | Accepted (owner) | S1-05 |
| [0014](ADR-0014-tenancy-model.md) | Tenancy model | Accepted (owner) | S1-06 |
| [0015](ADR-0015-attribute-definition-ownership.md) | Ownership of the attribute catalog | Proposed | S1-08 |
| [0016](ADR-0016-uuid-strategy.md) | Identifier (UUID) strategy | Accepted (owner) | S1-06 |
| [0017](ADR-0017-persistence-layout-and-conventions.md) | Persistence layout and dual-provider conventions | Accepted (owner) | S1-06 |
| [0018](ADR-0018-audit-atomicity-and-append-only-store.md) | Audit atomicity and append-only store | Proposed | S1-07 |
| [0019](ADR-0019-first-party-angular-ui-library.md) | First-party Angular UI library and AG Grid wrapper | Proposed | S1-09 |
| [0020](ADR-0020-in-process-mediator-validation-mapping.md) | In-process dispatch, validation and mapping libraries | Accepted (owner) | S1-02 |
| [0021](ADR-0021-messaging-library.md) | Messaging library | Proposed | P0-010 |
| [0022](ADR-0022-test-and-architecture-test-tooling.md) | Test and architecture-test tooling | Partly accepted (backend) | S1-02, S1-09 |
| [0023](ADR-0023-local-oidc-provider-and-cache.md) | Local OIDC provider, cache and local stack | Accepted (owner) | S1-03, S1-05 |
| [0024](ADR-0024-ci-platform-github-actions.md) | CI platform — GitHub Actions for PR checks | Accepted (owner) | S1-04 |

## Approval record
| ADR | Approver (name, role) | Date | Decision |
|---|---|---|---|
| 0011 | sivanandanr (repository owner) | 2026-11-07 | Accepted: CLAUDE.md 13 modules + Lifecycle. Independent architect review pending |
| 0012 | sivanandanr (repository owner) | 2026-11-08 | Accepted: ASP.NET Core BFF (OIDC + cookie + antiforgery), full login in Sprint 1, in-memory sessions. Security Architect review pending |
| 0013 | sivanandanr (repository owner) | 2026-11-08 | Accepted: separate IAM.Bff host with YARP; API bearer-only. Network owner/Security review pending |
| 0014 | sivanandanr (repository owner) | 2026-11-08 | Accepted: deployment per customer, no TenantId. Security Architect / product owner confirmation pending |
| 0016 | sivanandanr (repository owner) | 2026-11-08 | Accepted: application-generated UUIDv7. Database Architect review pending |
| 0017 | sivanandanr (repository owner) | 2026-11-08 | Accepted: conventions as proposed, hand-written snake_case naming. DBA review pending |
| 0020 | sivanandanr (repository owner) | 2026-11-07 | Accepted: hand-written dispatch, manual mapping, FluentValidation |
| 0022 | sivanandanr (repository owner) | 2026-11-07 | Partly accepted: xUnit v3 + Shouldly, reflection-based architecture tests |
| 0023 | sivanandanr (repository owner) | 2026-11-08 | Accepted: Keycloak, Valkey, PostgreSQL 17 + SQL Server 2022 Developer, Collector + Aspire dashboard (local only). Security/DevOps review and licence confirmation pending |
| 0024 | sivanandanr (repository owner) | 2026-11-08 | Accepted: GitHub Actions; gitleaks CLI, CodeQL, vulnerable-package gate. DevOps/Security review pending |
