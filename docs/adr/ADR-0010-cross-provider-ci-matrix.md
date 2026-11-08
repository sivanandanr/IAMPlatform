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
The job itself is the evidence. S1-06 adds `.github/workflows/pr.yml` job `db-parity` (legs `Database parity (SqlServer)` and `Database parity (PostgreSql)`, fail-fast off), running `tests/IAM.Persistence.Tests` with trait `Category=DatabaseParity` against Testcontainers started from the compose.yaml image digests. Setup per leg: bootstrap script as administrator, product migrations as `iam_migrator`, tests as per-module app logins.

Covered now: migrations apply + re-run; no pending model changes (both providers, main job); schema check of the probe table and the identity schema; "01" vs "1" and case distinct; padded/empty codes rejected; µs UTC round-trip and non-UTC rejected; stale Version rejected; audit columns server-set; unique-with-NULL parity; decimal(28,8); Unicode round-trip; readers not blocked by uncommitted writers; app login cannot create or drop tables or read another module's schema; migrator is not an administrator.

S1-07 adds `Parity/AuditParityTests` (ADR-0018): audit migration apply + re-run; audit row commits and rolls back with the business change; a failed audit write rolls back the business change; grant layer and trigger layer asserted separately by their distinct errors (module logins cannot read, update, delete, truncate or supply `Seq`; the audit login can read but not change; no runtime login can touch the audit migration history; triggers stop the schema owner).

Not covered yet: full normalised schema diff across every module (only the probe and identity schema today); NULL ordering; invalid JSON; outbox/inbox (P0-010); attribute registration without migration (S1-08).

Local evidence (2026-11-08, Windows 11, Docker, Release build): PostgreSQL 17.11 leg 23/23 passed; SQL Server 2022 CU27 leg 23/23 passed (3 min 37 s including image pull; licence accepted by the repository owner for this run). Mutation check on PostgreSQL: widening the module grants and removing the `Version` concurrency token failed 3 tests. S1-07 local evidence (2026-11-08), after the independent-review fixes: PostgreSQL 17.11 leg 39/39 passed (23 S1-06 + 16 audit). Single-layer mutation checks on PostgreSQL, each restored afterwards: removing only the triggers failed only the trigger test (1); widening only the grants (GRANT ALL to both audit roles) failed only the 3 grant-layer tests; removing only the writer's rollback failed only the 3 failed-write tests. SQL Server 2022 CU27 leg on the revised code: 39/39 passed (19 s; licence accepted by the repository owner for this run). Mutation checks were run on PostgreSQL only. S1-08 local evidence (2026-11-08), first version: PostgreSQL 17.11 leg 47/47 passed (8 attribute-catalog tests added; the identity-schema test now expects the catalog tables). Mutation checks on PostgreSQL, each restored: removing the catalog triggers failed only the 2 immutability tests; removing the draft concurrency check failed only the concurrency test; removing the registration audit write failed only the 2 audit tests. SQL Server 2022 CU27 leg for S1-08: 47/47 passed (23 s; licence accepted by the repository owner for this run). After the independent-review fixes: PostgreSQL 49/49 passed; mutation checks on PostgreSQL, each restored: removing the Draft-only CHECK failed the 2 publish tests; removing the version-identity check from the trigger failed only the identity test; putting the key pattern back to `$` failed only the trailing-newline key test. SQL Server 2022 CU27 leg on the revised code: 49/49 passed (30 s; licence accepted by the repository owner for this run). CI runs of both legs: pending the first PR.

SQL Server leg needs the repository variable `MSSQL_ACCEPT_EULA=Y`, set by the owner who accepts the Developer licence; without it the leg fails instead of accepting the licence silently.

## Open questions and approvals
- Pinned image versions; MI staging environment availability.
