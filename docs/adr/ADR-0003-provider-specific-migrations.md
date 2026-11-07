# ADR-0003: Provider-specific migrations and migration governance
Status: Proposed | Date: 2026-11-07 | Decision owners: Database Architect / DBA lead, Lead Solution Architect, DevOps lead (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 003), §7, §11; CLAUDE.md (separate reviewed migrations; no destructive migrations without approval). Drafted by Claude Code (S1-01); not approved.

## Context and requirements
DDL, index, JSON, collation and concurrency capabilities differ between engines. Blueprint §7 requires reviewed, forward-only migration bundles with preflight/rollback procedures and no runtime auto-migration in production.

## Options considered
1. Separate EF Core migration assemblies per provider (`backend/src/Persistence/SqlServer`, `backend/src/Persistence/PostgreSql`), each with per-module migrations (proposal).
2. One migration set with runtime provider branching.
3. Hand-written SQL scripts only (e.g. DbUp).

## Decision and rationale
Option 1. Provider-specific SQL (filtered indexes, CHECK constraints, collations, grants, triggers) lives in the provider's migrations. Rules:
- API and workers never call `Migrate()` / `EnsureCreated()`. Readiness fails if the expected latest migration is absent from the history table.
- CI generates per-provider idempotent SQL scripts attached to the PR for DBA review; a gated pipeline stage runs the reviewed script / bundle under the migrator identity.
- Forward-only with expand/contract for destructive changes. `Down()` is not used in production.
- Recovery = verified pre-migration backup / PITR point plus a compensating forward migration, rehearsed on a production-like restore.

## Security and privacy impact
Migrator identity is distinct from runtime identities (ADR-0017). Migrations never contain secrets or real data. Destructive migrations require explicit human approval (CLAUDE.md).

## SQL Server vs PostgreSQL impact
Lock/online behaviour differs: `LOCK_TIMEOUT` / `ONLINE=ON` on SQL Server; `lock_timeout` and `CREATE INDEX CONCURRENTLY` (outside a transaction) on PostgreSQL.

## Hybrid deployment impact
Migration pipeline agents need private network access to the database; no public exposure.

## Operational consequences and rollback
Two migration sets to maintain and review per change. Rollback via restore + compensating migration.

## Tests / evidence
Parity job (ADR-0010): migrations apply to empty DB, idempotent script re-run is a no-op, `HasPendingModelChanges()` false for every context/provider. Evidence: none yet.

## Open questions and approvals
- Blueprint "forward-only" vs database-architect role text "forward and recovery migrations": this ADR proposes restore + compensating forward migration as the recovery approach; needs confirmation (DL-020).
