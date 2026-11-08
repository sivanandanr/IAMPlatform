# ADR-0012: Browser session model — Backend-for-Frontend (BFF)
Status: Accepted by repository owner (sivanandanr, 2026-11-08, via Claude Code session) — independent Security Architect review pending | Date: 2026-11-07, decided 2026-11-08 | Decision owners: Security Architect, Lead Solution Architect, Frontend Architect (named approvers TBD)

Source: CLAUDE.md secure engineering standards (BFF preferred; otherwise ADR + threat model); SECURITY_BASELINE.md; Blueprint v1.0 §3 (YARP "OIDC/JWT validation"), §8; Spec v3.2 IAM-FR-024, §12.1. Decision log DL-003, DL-004. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
No tokens in localStorage/sessionStorage. OIDC Authorization Code + PKCE; validate issuer, audience, signature, lifetime, nonce/state. Spec §12.1: "Sign-in works with any identity provider, and routing decides who uses which" — possible multiple IdPs.

## Options considered
1. **BFF on standard ASP.NET Core** (OpenIdConnect + Cookie authentication + Antiforgery), tokens held server-side, YARP forwards API calls with server-attached identity.
2. Duende BFF framework (commercial licence).
3. SPA holds bearer tokens (requires exception ADR + threat model per CLAUDE.md).

## Decision and rationale
**Option 1 (owner, 2026-11-08)**, in a separate `IAM.Bff` host (ADR-0013). Sprint 1 depth: full OIDC login + secured sample API (DL-034). Session store: **in-memory** for Sprint 1 (owner decision) — single instance, sessions lost on restart; durable/shared store to be decided before any multi-instance deployment (R-022).

Implemented in S1-05 (`backend/src/Host/IAM.Bff`):
- Code flow, PKCE S256, `response_mode=form_post`, nonce + state validated by the ASP.NET Core handler; RS256 only; `SaveTokens=false`.
- On callback, tokens go to `BffSessionStore`; the `__Host-iam-session` cookie (Secure, HttpOnly, SameSite=Lax, Path=/) carries only `sub`, display name and a random 256-bit session id. Every request re-validates the session id, so logout/expiry revokes the cookie server-side. Idle 30 min, absolute 10 h, capacity bound (fail closed).
- Access token refreshed server-side 30 s before expiry, serialised per session (rotated refresh tokens used once); refresh failure ends the session. Token endpoint must be on the authority's origin (SSRF guard); 10 s timeout, 64 KB response bound.
- Antiforgery on every unsafe `/api/*` and `/bff/*` request (`X-XSRF-TOKEN` header; token delivered in the `__Host-XSRF-TOKEN` cookie by `GET /bff/user`).
- `GET /bff/login?returnUrl=` accepts local paths only; `POST /bff/logout` revokes the session, then RP-initiated logout with `id_token_hint`.
- Unauthenticated requests get 401, never a redirect; fallback authorization policy denies by default.
- Startup guard refuses the local test issuer, non-HTTPS authority or `RequireHttpsMetadata=false` outside Development (R-020); a missing client secret fails startup.

Session requirements (original list, retained):
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
Planned: tampered state, missing PKCE verifier, wrong issuer/audience, expired/unsigned token rejected; no tokens in browser storage (Playwright); session fixation; logout replay; open redirect; login CSRF; IdP mix-up.

S1-05 automated evidence (`tests/IAM.Bff.Tests`, `tests/IAM.Api.Tests`; in-memory hosts, no network): login redirect carries PKCE S256 + nonce + state + form_post and no secret; non-local return URLs rejected; anonymous requests get 401 and nothing is forwarded; cookie attributes; `/bff/user` returns no tokens; proxy replaces the client `Authorization` with the session token and strips `Cookie`, `X-User*`, `X-Roles`, `X-Original-*`, `Forwarded`, spoofed `X-Forwarded-For` and backend `Set-Cookie`; unsafe calls and logout without antiforgery get 400 and are not forwarded; revoked session gets 401; concurrent requests refresh once; failed refresh ends the session; logout revokes the session and sends `id_token_hint`; startup guards. API: wrong issuer/audience, expired, `alg=none`, HS256 and unknown key get 401; missing `sub` gets 403. Mutation check: disabling header stripping or antiforgery fails 3 tests.

S1-05 live evidence (2026-11-08, local Keycloak 26.8.2): API and BFF `/health/live` 200; anonymous `GET /bff/user`, proxied `/api/identity/me` and direct API call without a token all 401; `GET /bff/login` redirects to Keycloak via PAR (Keycloak accepted the client secret, redirect URI and PKCE parameters). **Interactive sign-in not verified:** the ASP.NET Core dev certificate is not trusted on the development machine, so the browser could not complete the login and the proxied `/api/identity/me` with a real token was not exercised. Repeat before S1-09 closes.

**Not covered yet:** tampered state / missing PKCE verifier at the callback (handled by ASP.NET Core, not separately tested); IdP mix-up (single IdP); Playwright browser-storage test (needs the SPA, S1-09); session fixation across login (new session id per login by construction, not tested).

## Open questions and approvals
- IdP choice and whether multiple IdPs / home-realm routing are required in Phase 0 (DL-004).
- MFA/step-up policy and session lifetimes (IAM-FR-024) — step-up stubbed and labelled not implemented in Sprint 1.
- Blueprint Week 1 "secure sample endpoint" vs P0-003 "OIDC login" — depth required in Sprint 1 (DL-034).
