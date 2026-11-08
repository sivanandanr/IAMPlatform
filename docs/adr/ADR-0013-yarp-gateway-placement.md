# ADR-0013: YARP gateway placement
Status: Accepted by repository owner (sivanandanr, 2026-11-08, via Claude Code session) — Network owner and Security Architect review pending | Date: 2026-11-07, decided 2026-11-08 | Decision owners: Lead Solution Architect, Security Architect, Network owner (named approvers TBD)

Source: Blueprint v1.0 §3, §4; CLAUDE.md stack (YARP). Decision log DL-003. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
YARP provides routing, headers and throttling (blueprint §3). Depends on ADR-0012 session model.

## Options considered
1. YARP inside `IAM.Api` (single host).
2. Separate `IAM.Bff` (BFF + YARP) host forwarding to `IAM.Api` on a private network.
3. Enterprise ingress / API management, no YARP.

## Decision and rationale
**Option 2 (owner, 2026-11-08).** `IAM.Bff` (login, session, antiforgery, YARP 2.3) forwards `/api/*` to `IAM.Api`, which accepts only bearer tokens (issuer, audience `iam-api`, RS256, lifetime) and denies by default. Locally the API listens on `127.0.0.1:5100`; in deployments it must be reachable only from the BFF (network rule, not enforceable in code).

Implemented in S1-05: request header stripping (`Authorization`, `Cookie`, `Proxy-Authorization`, `Forwarded`, `X-XSRF-TOKEN`, `X-User*`, `X-Role*`, `X-Original-*`, `X-Client-*`, `X-Auth*`); YARP overwrites `X-Forwarded-*`; backend `Set-Cookie` dropped; security headers (nosniff, `X-Frame-Options: DENY`, `frame-ancestors 'none'`, `default-src 'none'`, `no-store`, Referrer-Policy, Permissions-Policy, COOP); HSTS outside Development; 1 MB request body limit on both hosts.

**Not implemented yet:** per-route rate limiting, ForwardedHeaders known-proxy configuration for a real ingress, SPA CSP (S1-09).

Gateway requirements regardless of option: strip client-supplied `X-Forwarded-*`, `X-User*`, `X-Roles` and identity headers; trust only known proxies for ForwardedHeaders; security headers (strict CSP without unsafe-inline/eval, HSTS, nosniff, Referrer-Policy, `frame-ancestors 'none'`, Permissions-Policy); per-route rate limiting keyed on session/subject (IP fallback for anonymous); body size limits at gateway and Kestrel.

## Security and privacy impact
Option 2 adds a network hop but a clearer trust boundary; header spoofing is the main abuse case.

## SQL Server vs PostgreSQL impact
None.

## Hybrid deployment impact
Gateway in DMZ; API in a private segment.

## Operational consequences and rollback
Two deployables for the edge path; gateway config is versioned with releases.

## Tests / evidence
Injected `X-Forwarded-For` / `X-User-Id` does not change identity, audit IP or rate-limit key; header assertions against running gateway; 429 and 413 tests. Evidence: none yet.

## Open questions and approvals
- Does the customer already mandate an ingress/APIM layer?
