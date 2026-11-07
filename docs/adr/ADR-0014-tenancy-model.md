# ADR-0014: Tenancy model
Status: Proposed — **blocking** (S1-06) | Date: 2026-11-07 | Decision owners: Product / Business owner, Security Architect, Lead Solution Architect (named approvers TBD)

Source: Spec v3.2 "Rules that apply everywhere" ("Customers are kept apart … The database enforces this"), §12.11 ("same design serves SaaS and on-premises … Each customer's data is isolated"), IAM-FR-023, IAM-FR-025, IAM-FR-026 (audit includes tenant), FR-SEC-102; Blueprint v1.0 §8 ("If multi-tenant support is not approved, do not introduce tenant semantics unnecessarily"). Decision log DL-001. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
The spec requires per-customer isolation enforced by the database across queries, caches, queues, files, logs and jobs. The blueprint says not to add tenant semantics unless multi-tenancy is approved. This affects every key, index, cache key, queue and log from the first migration.

## Options considered
1. **Deployment-per-customer** (single-tenant stacks): isolation by separate database, broker vhost, cache and identities; no tenant column.
2. **Shared deployment with TenantId** on every tenant-owned row, EF global query filters, DB-level enforcement (SQL Server Row-Level Security / PostgreSQL RLS), tenant-prefixed cache keys and queues.
3. Both: TenantId present from day one but a single tenant per deployment initially.

## Decision and rationale
**Not decided — requires business decision.** Notes for decision makers:
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
If option 2/3: tenant A cannot read/write tenant B data via API, DB login, cache or queue on both engines. Evidence: none yet.

## Open questions and approvals
- Is SaaS multi-tenant hosting in scope for Release 1?
