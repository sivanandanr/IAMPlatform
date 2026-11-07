# ADR-0023: Local development OIDC provider and cache distribution
Status: Proposed — blocking (S1-03, S1-05) | Date: 2026-11-07 | Decision owners: Security Architect, DevOps lead; licence approval by procurement/legal (named approvers TBD)

Source: Blueprint v1.0 §3 (Redis; OIDC with Entra ID or approved IdP), §12 (local OIDC test provider in Docker Compose); CLAUDE.md (Redis for cache). Decision log DL-004, DL-018. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Developers need a local OIDC provider to exercise the BFF flow without real tenant credentials; a cache for immutable published policies and non-sensitive references only (blueprint §3).

## Options considered
- Local OIDC: Keycloak container (Apache-2.0) / OpenIddict-based dev server (Apache-2.0) / Duende IdentityServer (commercial).
- Cache: Redis (licence changed — RSAL/SSPL, AGPL option in 8.x; **verify**) / Valkey (BSD-3).

## Decision and rationale
**Not decided.** Recommendation: Keycloak for local/CI only, with realm config committed (no secrets; generated test users recorded in seed files, not in chat or docs); Valkey or Redis per legal review.

The local IdP must be impossible to enable outside Development (startup check on environment + issuer allowlist).

## Security and privacy impact
Test IdP issuer must never be trusted in non-dev environments. Cache never stores tokens, sessions with secrets, or sensitive attributes unless explicitly approved and isolated.

## SQL Server vs PostgreSQL impact
None.

## Hybrid deployment impact
None (dev only).

## Operational consequences and rollback
Swapping Redis ↔ Valkey is protocol-compatible for typical use.

## Tests / evidence
Startup fails if dev IdP issuer configured in non-Development environment. Evidence: none yet.

## Open questions and approvals
- Production IdP (Entra ID or other) and multi-IdP routing (spec §12.1) — DL-004.
- Licence approvals.
