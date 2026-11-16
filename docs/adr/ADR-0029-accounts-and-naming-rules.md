# ADR-0029: Accounts, linking existing accounts, and naming rules
Status: Proposed (decisions taken by the owner as DL-084 and DL-089, 2026-11-16) — stage 8 slice 2 implemented; human sign-off pending | Date: 2026-11-16 | Decision owners: IAM Domain lead, Security Architect (named approvers TBD)

Source: Spec v3.2 §3.1 (account types: Primary, Secondary, Service, Shared), §3.2 (each account has an owner and a status), §3.3 (matching accounts to people never guesses), §5.3 (every request item names its target account); CLAUDE.md rules 4, 8 and 11 and "Manual work"; DL-084f and DL-084g, DL-085, DL-086, DL-089. Drafted by Claude Code; not approved.

## Context and requirements
Provisioning (stage 8 slice 4) needs to know which account a grant goes to. A person can hold several accounts in one application but only one Primary. Some accounts already exist in the target; IAM must record whose they are without guessing. New Primary and Secondary accounts need a name, which a rule proposes and a workflow level may change (names only, never access).

## Options considered
1. Accounts as rows written by whoever registers them, audited only.
2. Accounts recorded only through a second-person link request (existing accounts) or by provisioning (accounts IAM creates), with uniqueness enforced by the database.
3. Leave existing accounts to discovery (a later stage).

## Decision and rationale
Option 2 (DL-089a). An `Account` belongs to one application and one person (holder or owner), with a type, a name (the login, letters, digits, dots, underscores and hyphens), the target's own id, a status and an origin. Who it belongs to, its type, application and origin never change. In this slice an account is recorded only from an approved link request naming it exactly; it starts Active and **not verified** until the target confirms it. One live Primary per person per application; live names (ignoring case) and target ids are unique per application. These are filtered unique indexes on both engines, and triggers refuse any other insert and any change of ownership.

Naming rules (DL-089b, DL-089c) exist per application for Primary and Secondary accounts. A rule is a typed template: literal text and published attributes with transforms from a fixed list (Lower, Upper, First n, RemoveAccents, LettersDigitsOnly) and a maximum length; no expressions or scripts (rule 8). Rules have numbered versions activated by a second person who neither proposed nor wrote the draft. Evaluating a rule fails closed: a missing attribute, an invalid or over-long result, or a name another live account already has (DL-086) gives no name, and a person then gives one.

HTTP: `GET /api/account/accounts/{id}`, `GET /api/account/applications/{id}/accounts[?owner=]`, `POST /api/account/link-requests`, `GET|POST /api/account/link-requests/{id}[/approve|reject|withdraw]`, `GET /api/account/applications/{id}/naming-rules/{type}`, `PUT .../draft`, `POST .../change-requests`, `GET|POST /api/account/naming-rule-change-requests/{id}[/approve|reject|withdraw]`. Contracts for other modules: `IAccountDirectory` (read) and `IAccountNaming` (propose a name).

## Security and privacy impact
Linking decides whose access an account carries, so it needs two people, and the person it belongs to can't approve it. Names and target ids are audited (they are identifiers, not secrets); no password fields exist. Attribute values used for naming are passed in by the caller and never stored or logged by this module.

## SQL Server vs PostgreSQL impact
Same tables, CHECKs, filtered unique indexes and triggers on both; names are compared through a lower-case key column checked against `LOWER(name)` (byte-wise on SQL Server). PostgreSQL also refuses `TRUNCATE`.

## Hybrid deployment impact
None in this slice.

## Operational consequences and rollback
A new schema and login (`account`, `iam_app_account`); the API needs `ConnectionStrings:account`. Additive; rollback is restore plus a compensating migration (DL-020).

## Tests / evidence
`AccountDomainTests` and `AccountParityTests` (both engines); endpoint permission map.

## Open questions and approvals
DL-085 (NHI as an account type), DL-086 (name collision rule beyond failing closed), account closure after last access (spec §3.2), discovery-based matching (spec §3.3). Approval: pending.
