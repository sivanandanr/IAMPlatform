# ADR-0002: One relational database engine per deployment
Status: Proposed | Date: 2026-11-07 | Decision owners: Lead Solution Architect, Database Architect / DBA lead (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 002), §7, §12; CLAUDE.md "One relational provider per deployment". Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Customers deploy on SQL Server / Azure SQL Managed Instance **or** PostgreSQL. Cross-engine active-active replication is out of Phase 0 scope (blueprint document control).

## Options considered
1. One engine per deployment, selected at startup; both engines supported by the product (proposal).
2. Dual-write to both engines.
3. Support only one engine.

## Decision and rationale
Option 1. `IAM_DB_PROVIDER` = `SqlServer` | `PostgreSql`, read once at startup. Any other value, or a missing value, fails startup — there is no default provider. No cross-engine dual-write.

## Security and privacy impact
A misconfigured provider must fail closed (no fallback). Connection strings come from secret references only (SEC-S1-12).

## SQL Server vs PostgreSQL impact
Every behaviour must be proven on both engines (ADR-0010); provider differences are handled in provider adapters and migrations (ADR-0003, ADR-0017).

## Hybrid deployment impact
Database stays on private networks; never internet-exposed.

## Operational consequences and rollback
Switching engines for an existing deployment is a data migration project, not a configuration change. Out of scope.

## Tests / evidence
Startup test: missing/invalid `IAM_DB_PROVIDER` → process exits non-zero with a non-secret error. Evidence: none yet.

## Open questions and approvals
- Supported versions: Azure SQL MI update policy (SQL 2022 vs Always-up-to-date), minimum on-prem SQL Server version, PostgreSQL major (17 vs 18). See DL-019.
