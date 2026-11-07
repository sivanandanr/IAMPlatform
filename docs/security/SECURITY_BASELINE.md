# IAM secure engineering baseline — mandatory design review

## 1. Identity, authentication, sessions
- Prefer standards-based enterprise OIDC federation (Entra ID/approved IdP), authorization code + PKCE. Do not create custom password/token protocols.
- Prefer BFF server-managed session for Angular: Secure + HttpOnly + appropriate SameSite cookie, rotation on authentication, idle/absolute expiry, server-side revocation; anti-CSRF token for unsafe methods; test login/logout/session fixation.
- If SPA bearer tokens are used instead, record exception ADR; memory-only tokens, strict refresh-token rotation and replay detection, secure refresh storage, XSS mitigation and logout semantics.
- Enforce MFA/step-up for privileged administration and sensitive policy changes; break-glass uses separate independent controls.
- Validate OIDC state/nonce, issuer, audience, signature algorithms, lifetime and key rollover; never accept unsigned tokens.

## 2. Angular/browser security
- Angular template binding and built-in escaping; avoid `bypassSecurityTrust*`, direct DOM insertion and `[innerHTML]` with untrusted content; review every exception.
- CSP with nonce/hash and restrictive script-src; HSTS; X-Content-Type-Options; Referrer-Policy; frame-ancestors; Permissions-Policy. Verify headers in deployment, not only in code.
- No secrets in browser bundles, source maps, browser storage, analytics or URL query strings. Protect identity exports and downloaded files.
- Frontend guards are UX only; API performs every authorization decision. Hide disallowed actions but never rely on hiding them.
- CSRF controls for cookie auth, strict CORS origins and credentials; prevent open redirects; validate upload type/content and file names server-side.
- Accessibility, safe error messages, rate-limited search and masked sensitive fields; sanitize CSV/Excel exports against formula injection.

## 3. .NET API security
- Require authenticated endpoints by default; explicit reviewed anonymous exceptions (health probes separated into public liveness and protected readiness).
- Resource/action authorization: actor, beneficiary, application, entitlement, organization, workflow state and separation-of-duties. Check at domain layer for background jobs too.
- Validate DTOs, max lengths, enum values, date ranges, pagination caps, content types, request body limits and tenant/environment boundaries if applicable.
- Return ProblemDetails without stack traces, SQL, internal hostnames or user enumeration details. Add correlation IDs and safe logs.
- Use parameterized EF/Dapper queries, approved dynamic ordering fields, bounded transactions, concurrency tokens and migration approval.
- Protect against SSRF: strict scheme/host allowlists, DNS/IP checks, redirects policy, metadata endpoint blocking and egress firewall; never let users set arbitrary connector URLs.
- Apply rate limits, timeouts, cancellation, retry budgets and circuit breakers; avoid retrying non-idempotent operations without idempotency keys.
- Uploads: allowlisted MIME and magic bytes, max size, antivirus scanning, quarantine, random storage names, signed access, retention policy.

## 4. IAM-specific policy and workflow protections
- Use immutable policy versions and cryptographic digest of published AST; maker/checker must be distinct identities and independent of affected privileged decisions.
- Matrix rules reference registered typed attributes and allowlisted operators; restrict nesting, expression size, evaluation time and regex if ever added.
- `EESubgroupCode` is a string; preserve leading zeroes and source provenance. Missing/stale attributes fail closed or controlled escalation.
- Snapshot evaluated inputs, selected rules, resolved approvers, workflow version, decision and reason; redact/encrypt sensitive fields as required.
- No self-approval or bypass of mandatory SoD/security stages; parallel quorum evaluated transactionally; timeout is escalation not approval.
- Approved birthright access skips individual approval, **not** SoD/eligibility/audit checks. Separate non-birthright request items.
- Privileged/break-glass must be independently monitored, time-bound, alerted and reviewed after use.

## 5. Data, connectors, secrets, messaging
- Choose one relational DB per deployment; SQL Server and PostgreSQL parity tested in CI. Separate migration projects; no automatic production migrations.
- Encrypt transport and data at rest; use vault-backed secret references, rotation, scoped managed identities and per-connector least privilege.
- Minimize PII and HR attribute copies; document classification, retention, legal hold, data residency and erasure obligations.
- Broker: TLS, dedicated vhosts/users, no guest in production, authorization per queue, encrypted payloads where necessary; do not put tokens/secrets on events.
- Outbox/inbox, idempotent handlers, correlation IDs, bounded retries, DLQ and controlled replay; never infer deletion from incomplete discovery.
- Private hybrid agents use outbound authenticated connectivity; prohibit direct inbound access to on-prem AD/SAP where feasible.

## 6. Supply chain, deployment and operations
- Pin SDKs, package versions and base images (digest for production); review licenses, generate SBOM, scan dependencies/images and sign artifacts.
- CI protected branches, independent approval, short-lived workload credentials, no production secrets in PR jobs; protect artifact provenance.
- Isolate dev/test/prod networks, databases, secrets and identities; deploy least privilege and immutable artifacts; backups encrypted and restore-tested.
- OpenTelemetry traces must not capture passwords, tokens, raw HR records or sensitive approval payloads.
- Test disaster recovery, key rotation, connector outage, partial discovery, privilege escalation, concurrency and denial-of-service scenarios.

## 7. Required security tests
- Unauthenticated/unauthorized access, IDOR/BOLA, horizontal/vertical privilege escalation, CSRF, XSS, injection, SSRF, open redirect, unsafe deserialization, mass assignment.
- Matrix ambiguity, unknown/inactive attributes, null/stale values, approver conflict, maker-checker bypass, replay, policy-version mismatch and self-approval.
- Concurrent approvals, duplicate provisioning messages, connector timeout, DLQ replay, broken correlation, secret leakage in errors/logs.
- Run DAST and penetration testing before production; use OWASP ASVS, API Security Top 10 and current organizational controls as review references, not as automatic compliance claims.

## Detailed ingress defenses
Mandatory: `docs/security/BACKEND_INPUT_VALIDATION.md` applies to HTTP, message, webhook, file and connector ingress.
