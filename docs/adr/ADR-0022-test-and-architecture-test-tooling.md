# ADR-0022: Test, assertion and architecture-test tooling
Status: Partially accepted — backend unit/architecture tooling accepted by repository owner (sivanandanr, 2026-11-07); frontend, integration and load tooling still Proposed | Date: 2026-11-07 | Decision owners: QA lead, Lead Solution Architect, Frontend Architect; licence approval by procurement/legal

Source: Blueprint v1.0 §3 (xUnit, "FluentAssertions-equivalent", Testcontainers, Playwright, k6; "Avoid unnecessary license dependencies"); CLAUDE.md dependency rule. Decision log DL-018. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Backend unit/integration/architecture tests, frontend component/E2E/a11y tests, load tests.

## Options considered
- Assertions: FluentAssertions v8 (commercial licence from Xceed — verify) / AwesomeAssertions fork / Shouldly / plain xUnit asserts.
- Architecture tests: NetArchTest.Rules (MIT; maintenance status to verify) / ArchUnitNET (Apache-2.0) / plain reflection over assembly references (no package).
- Integration: Testcontainers (MIT) for `mssql` and `postgres`.
- Frontend unit: Vitest via Angular CLI unit-test builder / Jest / Karma (deprecated).
- E2E + visual: Playwright. Accessibility: axe-core + `@axe-core/playwright` (MPL-2.0).
- Load: k6 (AGPL-3.0 — legal review) / alternatives.

## Decision and rationale
**Decided for backend (repository owner, 2026-11-07):** xUnit v3 + Shouldly; architecture tests via plain reflection over assembly references (no package). Licences (Apache-2.0 / BSD) to be verified when added; versions pinned centrally.

**Implementation note (S1-02, 2026-11-07):** the .NET 10 SDK no longer runs Microsoft.Testing.Platform-based test projects through VSTest, so `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }` and the VSTest adapter packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`) are not used. Packages in use: `xunit.v3` 4.0.2 (Apache-2.0) and `Shouldly` 4.3.0 (BSD-3-Clause), licences read from the package nuspec.

**Decided (owner, 2026-11-08, S1-06):** Testcontainers.MsSql and Testcontainers.PostgreSql 4.16.0 (MIT) for database parity tests, using the same pinned image digests as compose.yaml. The SQL Server container's licence acceptance comes from the runner's `MSSQL_ACCEPT_EULA`, never from the test code.

**Still open:** Vitest / Playwright / axe-core (S1-09), k6.

Original recommendations: xUnit + Shouldly or AwesomeAssertions; architecture rules via plain reflection first (ArchUnitNET if richer rules needed); Testcontainers; Vitest; Playwright; axe-core. k6 subject to legal review of AGPL use in CI (not distributed).

## Security and privacy impact
Test fixtures synthetic only. Test containers must not use production credentials.

## SQL Server vs PostgreSQL impact
Testcontainers for both (ADR-0010).

## Hybrid deployment impact
None.

## Operational consequences and rollback
Swapping assertion libraries is mechanical but noisy; decide before tests accumulate.

## Tests / evidence
N/A.

## Open questions and approvals
- Licence verification for each package.
