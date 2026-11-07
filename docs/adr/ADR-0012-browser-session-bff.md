# ADR-0012: Browser session model — Backend-for-Frontend (BFF)
Status: Proposed — **blocking** (S1-05) | Date: 2026-11-07 | Decision owners: Security Architect, Lead Solution Architect, Frontend Architect (named approvers TBD)

Source: CLAUDE.md secure engineering standards (BFF preferred; otherwise ADR + threat model); SECURITY_BASELINE.md; Blueprint v1.0 §3 (YARP "OIDC/JWT validation"), §8; Spec v3.2 IAM-FR-024, §12.1. Decision log DL-003, DL-004. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
No tokens in localStorage/sessionStorage. OIDC Authorization Code + PKCE; validate issuer, audience, signature, lifetime, nonce/state. Spec §12.1: "Sign-in works with any identity provider, and routing decides who uses which" — possible multiple IdPs.

## Options considered
1. **BFF on standard ASP.NET Core** (OpenIdConnect + Cookie authentication + Antiforgery), tokens held server-side, YARP forwards API calls with server-attached identity.
2. Duende BFF framework (commercial licence).
3. SPA holds bearer tokens (requires exception ADR + threat model per CLAUDE.md).

## Decision and rationale
**Not decided.** Recommendation: option 1 (no licence cost, meets CLAUDE.md preference). Session requirements if accepted:
- Cookie `__Host-` prefix, Secure, HttpOnly, SameSite=Lax or Strict, Path=/; session ID rotated at login; idle and absolute timeouts; server-side revocation on logout (POST + antiforgery).
- PKCE S256; signing-algorithm allowlist (reject `none`, HS* with RSA keys); issuer pinned per configured IdP; key rollover supported.
- `returnUrl` local-only (open-redirect protection).
- Angular uses same-origin relative URLs with XSRF header; no CORS credentials to other origins.

## Security and privacy impact
Removes token theft via XSS from browser storage; introduces CSRF exposure, mitigated by antiforgery + Origin checks + SameSite.

## SQL Server vs PostgreSQL impact
If server-side session store uses the database, parity tests apply; otherwise Redis (ADR-0023) — sessions are sensitive and must not be cached alongside non-sensitive data without isolation.

## Hybrid deployment impact
BFF sits in the DMZ/cloud segment; IdP reachable outbound.

## Operational consequences and rollback
Sticky sessions not required if the session/ticket store is shared. Key ring for Data Protection must be shared and protected.

## Tests / evidence
Tampered state, missing PKCE verifier, wrong issuer/audience, expired/unsigned token rejected; no tokens in browser storage (Playwright); session fixation; logout replay; open redirect; login CSRF; IdP mix-up. Evidence: none yet.

## Open questions and approvals
- IdP choice and whether multiple IdPs / home-realm routing are required in Phase 0 (DL-004).
- MFA/step-up policy and session lifetimes (IAM-FR-024) — step-up stubbed and labelled not implemented in Sprint 1.
- Blueprint Week 1 "secure sample endpoint" vs P0-003 "OIDC login" — depth required in Sprint 1 (DL-034).
