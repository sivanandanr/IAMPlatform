# ADR-0010: Cross-provider CI parity matrix
Status: Proposed | Date: 2026-11-07 | Decision owners: Database Architect, QA lead, DevOps lead (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 010), §7, §11; CLAUDE.md (CI parity tests for both providers). Drafted by Claude Code (S1-01); not approved.

## Context and requirements
SQL Server and PostgreSQL must pass the same domain, migration, workflow and data-integrity tests (blueprint §16). SQLite must not substitute for provider parity (blueprint §7).

## Options considered
1. Testcontainers for `mssql` and `postgres` (images pinned by digest), shared abstract test classes with provider subclasses, CI matrix with both legs required (proposal).
2. Shared long-lived test databases.
3. EF InMemory/SQLite for speed.

## Decision and rationale
Option 1. Tests run as the least-privilege app login (never `sa`/`postgres`); migrations as the migrator login. Fail-fast off so both legs report. Architecture test fails the build if any project references `Microsoft.EntityFrameworkCore.Sqlite` or `.InMemory`.

CI parity job asserts: migrations apply + idempotent re-run; no pending model changes; normalised schema diff (columns, nullability, lengths, keys, CHECKs, indexes) empty except a reviewed allowlist; behaviours — "01" vs "1" distinct, case/trailing-space handling, µs timestamp round-trip, stale Version → concurrency exception, unique-with-NULL parity, deterministic NULL ordering, invalid JSON rejected, outbox rollback, inbox dedupe, audit UPDATE/DELETE denied, Unicode round-trip, attribute registration needs no migration.

## Security and privacy impact
Test fixtures are synthetic only; no real identity data.

## SQL Server vs PostgreSQL impact
Core of this ADR. A container is **not** Azure SQL MI; MI-specific behaviour requires a staging smoke test against real MI.

## Hybrid deployment impact
CI agents need Docker; SQL Server container uses Developer edition (non-production licence — DL-018).

## Operational consequences and rollback
Longer CI times; mitigate with parallel legs and container reuse within a job.

## Tests / evidence
The job itself is the evidence; none yet.

## Open questions and approvals
- Pinned image versions; MI staging environment availability.
