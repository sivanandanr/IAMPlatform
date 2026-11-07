# ADR-0022: Test, assertion and architecture-test tooling
Status: Proposed — blocking (S1-02, S1-09) | Date: 2026-11-07 | Decision owners: QA lead, Lead Solution Architect, Frontend Architect; licence approval by procurement/legal (named approvers TBD)

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
**Not decided.** Recommendations: xUnit + Shouldly or AwesomeAssertions; architecture rules via plain reflection first (ArchUnitNET if richer rules needed); Testcontainers; Vitest; Playwright; axe-core. k6 subject to legal review of AGPL use in CI (not distributed).

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
