# Definition of Done (each vertical slice)
- [ ] Requirement IDs traced to implementation and acceptance tests.
- [ ] ADR and threat model updated for meaningful design/security changes.
- [ ] Authentication and server-side resource/action authorization verified; negative tests included.
- [ ] Input validation, safe errors, logging redaction, rate limits, concurrency and idempotency reviewed.
- [ ] SQL Server and PostgreSQL migration/integration parity passed (or explicitly blocked with issue).
- [ ] No secret, PII dump, production connection or unapproved dependency committed.
- [ ] Unit, integration, contract, UI and security tests run as applicable; real results recorded.
- [ ] Audit event and trace correlation tested; operational alerts/runbooks updated.
- [ ] Independent reviewer and security sign-off for sensitive changes.
- [ ] Rollback/recovery strategy documented; no false production-readiness claims.

## P0 backend validation gate
All write APIs, message consumers, webhooks and imports require server-side DTO validation, object-level authorization, bounded inputs, safe persistence, domain invariants, sanitized audit, negative tests and SQL Server/PostgreSQL parity. See `docs/security/BACKEND_INPUT_VALIDATION.md`.

## Frontend component quality gate
Common controls use approved in-house shared UI library; AG Grid only through approved wrapper. CVA/form integration, WCAG 2.2 AA target, keyboard/ARIA, server validation display, XSS-safe rendering, responsive behavior and automated tests are mandatory. No Kendo/PrimeNG or other unapproved UI package dependencies.
