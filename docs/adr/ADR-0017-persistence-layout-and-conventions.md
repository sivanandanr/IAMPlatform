# ADR-0017: Persistence layout and dual-provider conventions
Status: Accepted by repository owner (sivanandanr, 2026-11-08, via Claude Code session) — DBA review pending | Date: 2026-11-07, decided 2026-11-08 | Decision owners: Database Architect / DBA lead, Lead Solution Architect (named approvers TBD)

Source: Blueprint v1.0 §6, §7; CLAUDE.md (no cross-module direct table writes; least-privilege DB identities). Drafted by Claude Code (S1-01); not approved.

## Context and requirements
One logical model, provider-specific physical DDL; identical behaviour on SQL Server and PostgreSQL.

## Options considered
Per concern below; the proposal is listed first.

## Decision and rationale
**Accepted as proposed (owner, 2026-11-08)**, with a hand-written snake_case convention instead of `EFCore.NamingConventions`.

Implemented in S1-06:
- `IAM.BuildingBlocks.Persistence` (EF Core Relational only, no driver): `IamDbContext` base (one lower-case schema per module, allowlisted name), conventions, `AuditingSaveChangesInterceptor`, `IAM_DB_PROVIDER` parsing (exact `SqlServer` / `PostgreSql`, no default, fails closed).
- `IAM.Persistence.SqlServer` / `IAM.Persistence.PostgreSql`: provider setup and each provider's reviewed migrations; migration history table per module schema (`__EFMigrationsHistory` / `__ef_migrations_history`). No automatic retry.
- Audit columns and `Version` are **shadow properties**, so no entity setter or DTO can bind them. The interceptor sets them from `TimeProvider` and the token `sub`; it discards changes to Created*, and saving without an actor fails.
- `DateTimeOffset` stored as `datetimeoffset(6)` / `timestamptz(6)`; non-UTC values are rejected, not converted.
- `IsCode(maxLength)`: `Latin1_General_100_BIN2` / `COLLATE "C"` plus a CHECK that rejects empty values and leading/trailing spaces on both engines.
- The model cache is keyed by provider, so one process can never reuse a SQL Server model for PostgreSQL.
- `IAM.Api` refuses to start without `IAM_DB_PROVIDER` and a `ConnectionStrings:<schema>` per registered module. It never runs migrations.
- Least-privilege bootstrap scripts: `infra/db/sqlserver/bootstrap-module.sql`, `infra/db/postgresql/bootstrap-module.sql` (inputs bound as parameters; identifiers allowlisted and quoted).

**Implemented in S1-07:** `iam_audit_writer` and `iam_audit_reader` roles (`infra/db/*/grant-audit-role.sql`) and the `IsAppendOnly()` convention (no Created*/Modified*/Version columns; interceptor refuses update/delete) — ADR-0018. **Not implemented yet:** `iam_readonly` (later); `ISJSON`/`jsonb` columns (no JSON yet); keyset pagination and NULL-ordering helpers; raw-SQL analyzer/test ban; readiness check for the latest migration (ADR-0003).

Proposed conventions (as approved):
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
Parity behaviours listed in ADR-0010. S1-06 evidence: `tests/IAM.Persistence.Tests` (model tests in the main job; `DatabaseParityTests` per engine in the `Database parity` job).

**Known gap:** the application login can modify its module's migration history table (schema-level grant on SQL Server, default privileges on PostgreSQL). Tracked as R-024.

## Open questions and approvals
- ~~Approve `EFCore.NamingConventions` or hand-written convention.~~ Hand-written (owner, 2026-11-08).
- One schema + migration history per module across two engines: DBA review of operational overhead.
