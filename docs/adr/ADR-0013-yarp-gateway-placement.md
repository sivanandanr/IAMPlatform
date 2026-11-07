# ADR-0013: YARP gateway placement
Status: Proposed — **blocking** (S1-05) | Date: 2026-11-07 | Decision owners: Lead Solution Architect, Security Architect, Network owner (named approvers TBD)

Source: Blueprint v1.0 §3, §4; CLAUDE.md stack (YARP). Decision log DL-003. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
YARP provides routing, headers and throttling (blueprint §3). Depends on ADR-0012 session model.

## Options considered
1. YARP inside `IAM.Api` (single host).
2. Separate `IAM.Bff` (BFF + YARP) host forwarding to `IAM.Api` on a private network.
3. Enterprise ingress / API management, no YARP.

## Decision and rationale
**Not decided.** Recommendation: option 2 — keeps browser session concerns and the public edge separate from the API, and lets the API be reachable only from the gateway (network rule).

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
