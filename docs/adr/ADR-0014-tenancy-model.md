# ADR-0014: Tenancy model
Status: Accepted by repository owner (sivanandanr, 2026-11-08, via Claude Code session) — Security Architect and product owner confirmation pending | Date: 2026-11-07, decided 2026-11-08 | Decision owners: Product / Business owner, Security Architect, Lead Solution Architect (named approvers TBD)

Source: Spec v3.2 "Rules that apply everywhere" ("Customers are kept apart … The database enforces this"), §12.11 ("same design serves SaaS and on-premises … Each customer's data is isolated"), IAM-FR-023, IAM-FR-025, IAM-FR-026 (audit includes tenant), FR-SEC-102; Blueprint v1.0 §8 ("If multi-tenant support is not approved, do not introduce tenant semantics unnecessarily"). Decision log DL-001. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
The spec requires per-customer isolation enforced by the database across queries, caches, queues, files, logs and jobs. The blueprint says not to add tenant semantics unless multi-tenancy is approved. This affects every key, index, cache key, queue and log from the first migration.

## Options considered
1. **Deployment-per-customer** (single-tenant stacks): isolation by separate database, broker vhost, cache and identities; no tenant column.
2. **Shared deployment with TenantId** on every tenant-owned row, EF global query filters, DB-level enforcement (SQL Server Row-Level Security / PostgreSQL RLS), tenant-prefixed cache keys and queues.
3. Both: TenantId present from day one but a single tenant per deployment initially.

## Decision and rationale
**Option 1, deployment per customer (owner, 2026-11-08).** Release 1 does not host several customers in one deployment. Each customer gets its own database, broker vhost, cache and identities; there is no TenantId column, no tenant query filter and no row-level security. Isolation therefore depends on deployment separation (separate connection strings, credentials and networks), which deployment automation and review must enforce.

If shared SaaS hosting is ever required, this ADR must be superseded **before** any customer data is shared: adding TenantId later is a large migration on both engines (R-001).

Notes considered at decision time:
- Option 1 is simplest and strongest for on-prem; SaaS cost scales per customer.
- Option 2 satisfies "the database enforces this" for shared SaaS but adds RLS on two engines and many negative tests.
- Option 3 avoids a later rewrite but carries the complexity before it is needed.

## Security and privacy impact
Cross-tenant data exposure is a critical risk in options 2/3; requires RLS + query filters + negative tests on both engines.

## SQL Server vs PostgreSQL impact
RLS syntax and behaviour differ (security policies + predicate functions vs `CREATE POLICY`); partitioned unique keys must include TenantId on PostgreSQL if partitioned by tenant.

## Hybrid deployment impact
Spec §12.11 says agents run inside each customer's network — consistent with any option.

## Operational consequences and rollback
Adding TenantId after data exists is a large migration; decide before P0-006.

## Tests / evidence
Option 1: no tenant semantics in code. S1-06 parity tests show module logins cannot read other module schemas; per-customer separation is a deployment control and needs a deployment-time check (not yet built).

## Open questions and approvals
- Is SaaS multi-tenant hosting in scope for Release 1?
