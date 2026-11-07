# ADR-0017: Persistence layout and dual-provider conventions
Status: Proposed — blocking (S1-06) | Date: 2026-11-07 | Decision owners: Database Architect / DBA lead, Lead Solution Architect (named approvers TBD)

Source: Blueprint v1.0 §6, §7; CLAUDE.md (no cross-module direct table writes; least-privilege DB identities). Drafted by Claude Code (S1-01); not approved.

## Context and requirements
One logical model, provider-specific physical DDL; identical behaviour on SQL Server and PostgreSQL.

## Options considered
Per concern below; the proposal is listed first.

## Decision and rationale
Proposed conventions:
- **Layout**: one DbContext per module, one schema per module (lower-case on both engines), `__EFMigrationsHistory` per module in its schema; migrations per provider assembly (ADR-0003).
- **Audit columns** on every row: CreatedAtUtc, CreatedBy, ModifiedAtUtc, ModifiedBy, Version — set by a SaveChanges interceptor from the server-side actor and `TimeProvider`; never bound from DTOs.
- **Concurrency**: application `Version bigint` concurrency token (not `rowversion` / `xmin`) for identical semantics.
- **Time**: `DateTimeOffset` in domain; `datetimeoffset(6)` / `timestamptz`; converter enforces offset 0 and µs truncation.
- **Codes**: always strings; binary collation (`Latin1_General_100_BIN2` / `COLLATE "C"`); normalisation in application per AttributeDefinition; SQL Server CHECK blocks trailing spaces (`DATALENGTH`). Display text uses default collation; search uses a normalised column.
- **JSON**: only for AST and payloads, never query-critical; `nvarchar(max)` + `ISJSON` CHECK / `jsonb`; hashes over application-canonical text, never over a `jsonb` read-back.
- **Unique + NULL**: filtered/partial unique indexes `WHERE col IS NOT NULL` so behaviour matches.
- **NULL ordering**: explicit null-rank in ORDER BY + Id tiebreaker; keyset pagination.
- **Isolation**: SQL Server `READ_COMMITTED_SNAPSHOT` on (DBA/migrator step); correctness from optimistic concurrency + unique constraints; retry only idempotent units on 1205/3960 and 40001/40P01.
- **Decimal**: (28,8).
- **Naming**: PascalCase on SQL Server, snake_case on PostgreSQL via convention (package `EFCore.NamingConventions` needs approval — or hand-written convention).
- **DB identities**: `iam_migrator` (owns module schemas, not sysadmin/superuser); `iam_app_<module>` (DML on own schema only); `iam_audit_writer` (INSERT/SELECT on `audit`); `iam_readonly`. PostgreSQL: `REVOKE CREATE ON SCHEMA public`, `ALTER DEFAULT PRIVILEGES`.
- **Raw SQL**: parameterized only; Dapper SQL per provider; analyzer + test ban interpolated `FromSqlRaw`/`ExecuteSqlRaw`.

## Security and privacy impact
Per-module identities enforce "no cross-module writes" in the database. `EnableSensitiveDataLogging` off outside local dev.

## SQL Server vs PostgreSQL impact
Core of this ADR.

## Hybrid deployment impact
None.

## Operational consequences and rollback
Many schemas/identities increase DBA setup; scripted in migrations/infra.

## Tests / evidence
Parity behaviours listed in ADR-0010. Evidence: none yet.

## Open questions and approvals
- Approve `EFCore.NamingConventions` or hand-written convention.
- One schema + migration history per module across two engines: DBA review of operational overhead.
