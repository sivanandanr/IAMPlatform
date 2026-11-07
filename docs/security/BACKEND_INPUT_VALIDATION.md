# Mandatory Backend Request Validation and Payload Defense (P0)

Applies to ALL ingress: ASP.NET Core Minimal APIs, YARP-facing routes, webhook callbacks, worker messages, import files, scheduled sync, connector responses, admin configuration and policy expressions. Frontend validation is UX only. All enforcement is server-side.

## Threats and invariant
Prevent mass assignment, malformed or oversized payloads, SQL/NoSQL/LDAP/command injection, XSS, SSRF, path traversal, unsafe deserialization, replay, privilege escalation, workflow bypass, BOLA/IDOR and denial-of-service. Never treat input validation as a substitute for authorization or safe output encoding.

## Request processing order
1. Enforce HTTPS/TLS, trusted proxy/forwarded headers, strict routing and HTTP method; reject unexpected content types.
2. Enforce reverse-proxy and app-level request body size limits; bound multipart file count/size and streaming memory use. Return 413 for oversize.
3. Authenticate identity and validate session/JWT/OIDC correctly.
4. Authorize action AND resource ownership/scope; never infer permissions from a request-supplied role, owner, approval state or identity ID.
5. Bind into narrow request DTOs (never EF/domain entities); reject unexpected fields for sensitive operations when practical; set required fields and limits.
6. Validate shape, type, ranges, length, enum allowlists, string normalization, list counts, date ranges and cross-field constraints. For invalid input return 400/422 ProblemDetails with safe field errors, not exception details.
7. Enforce domain invariants again inside command handlers (policy version, entitlement eligibility, maker-checker, SoD, no self-approval, state transitions, concurrency).
8. Use parameterized queries, EF Core LINQ, safe ORM mappings; for dynamic sort/filter use server-owned allowlists, not raw SQL fragments.
9. Encode on output by context. Angular default interpolation is escaped; do not use bypassSecurityTrust*, direct innerHTML or unsanitized HTML. HTML sanitization is not SQL injection defense.
10. Emit sanitized security/audit metadata (actor, action, correlation, rule ID, outcome); never raw malicious payloads, tokens, passwords, HR records or connection strings.

## Payload rules
- Default deny unknown properties for privileged endpoints; consider forward-compatibility exceptions explicitly and document them.
- Whitelist fields per operation: CreateIdentityRequest, UpdateIdentityRequest, PublishPolicyRequest, ApproveRequest, etc. Never accept IsAdmin, Status=Approved, PolicyPublished, ApproverId, CreatedBy, TenantId, or privilege-bearing server fields from untrusted clients unless explicitly authorized and verified.
- Validate JSON nesting depth, array counts, field lengths, number ranges and total request bytes. Avoid unbounded recursion and catastrophic-regex patterns; use timeouts for complex patterns.
- Reject malformed UTF-8, invalid JSON and duplicate keys for security-sensitive JSON where parser behavior is ambiguous; test the chosen serializer behavior. Normalize Unicode only per documented attribute semantics; preserve leading-zero HR codes as strings.
- Do not blacklist characters as primary defense; apostrophes, angle brackets and SQL-like strings may be legitimate data. Apply field-specific allowlists only for constrained identifiers/codes, and parameterize SQL.
- On file upload: allowlist types/extensions, inspect magic bytes, scan for malware, generate server-side filenames, store outside webroot, limit size, avoid zip bombs and prevent path traversal.
- For URL/host inputs: reject loopback, link-local, private and metadata IPs unless explicitly authorized for private connectors; validate DNS resolution/rebinding, redirect chains, schemes and ports; outbound egress allowlists.
- XML/SAML/LDAP/SAP integrations: disable external entities/DTD unless explicitly needed, use safe parsers, LDAP filter escaping, allowlisted RFC/commands and bounded reads.
- Webhooks: verify signature, timestamp/replay window and event ID deduplication before processing.
- Workers/messages: validate message schemas, versions, authorized producers, signature/trust boundary where relevant, size and idempotency. Internal messages are not inherently trusted.

## ASP.NET Core implementation pattern
- Endpoint group filters or validation pipeline for DTO checks; do not rely on endpoint filters for resource authorization or domain invariants.
- FluentValidation or built-in validators with explicit rules; ensure validators execute for ALL endpoint paths and message consumers. Do not assume package installation automatically invokes validators.
- Configure JsonSerializerOptions.MaxDepth; ASP.NET Core server MaxRequestBodySize; Kestrel/reverse proxy limits; per-route rate limiting; cancellation tokens; response timeouts.
- Use explicit JSON DTOs and Mapster/manual mappings with allowed properties; never blindly map client JSON onto tracked EF entities.
- Use `Results.ValidationProblem` for field failures and standardized ProblemDetails for all errors; avoid returning stack traces and raw DB errors.
- Validate and authorize pagination and exports; cap page size, date windows and maximum export rows.
- Use EF Core parameterization, transactions and optimistic concurrency. If raw SQL is necessary, parameterize values and allowlist table/column/order names.
- Every approval/provisioning state change requires authenticated actor, permission, object-level scope, current state/version and audit evidence.
- Ensure JWT claim data is not accepted as a substitute for current authorization when revocation, role changes or high-risk operations require fresh checks.

## Angular and API boundary
- Typed reactive forms with input length and format guidance; display server validation errors by field.
- Do not rely on route guards, hidden buttons or disabled controls for security.
- Prefer BFF with HttpOnly Secure SameSite cookies and antiforgery token for unsafe methods; if bearer token SPA is chosen, document threat model.
- CSP, trusted types where feasible, no unsafe DOM manipulation; strict CORS allowlist; secure response headers.
- Never place secrets or authorization decisions in frontend environment files.

## Dual-provider parity
Run equivalent negative and valid-payload integration tests on SQL Server and PostgreSQL. Verify unique/length/collation/decimal/UTC semantics, concurrency and parameterized queries. Provider-specific exceptions must not leak to clients.

## Mandatory negative tests
1. Unknown/mass-assignment properties such as `isAdmin`, `approved`, `approverId`, `ownerId`.
2. SQL injection payload in names, filters and sort keys; prove data remains safe and sort keys are rejected.
3. HTML/JS payload stored and displayed safely; no executable script.
4. Missing, null, overlength, invalid enum, negative/out-of-range numbers, huge arrays and deep JSON.
5. Invalid content-type, oversized JSON/multipart and unsupported file signatures.
6. BOLA/IDOR: user accesses another identity/request; correct 403/404 per policy.
7. Self-approval, maker-checker bypass, stale policy version, duplicate approval/replay, unauthorized status transition.
8. Dynamic policy AST unknown attribute/operator, invalid types, too many nested groups, regex abuse, no-match/ambiguity.
9. SSRF to localhost/private/metadata IPs, redirects and DNS rebinding.
10. Duplicate webhook/message and malformed external connector data.
11. Request throttling and concurrency stress.
12. Error messages and audit logs contain no credentials, stack traces or full rejected payloads.

## Definition of Done
No new write endpoint, worker consumer, connector parser or import handler is complete without: request contract, server-side validation, resource authorization, domain checks, bounded input, safe persistence, safe error handling, audit, positive/negative tests and DB parity where persistence is affected. Security reviewer sign-off required for sensitive workflows.
