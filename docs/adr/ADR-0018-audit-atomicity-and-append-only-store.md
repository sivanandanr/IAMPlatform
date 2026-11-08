# ADR-0018: Audit atomicity and append-only audit store
Status: Accepted (owner decision, 2026-11-08) — implementation revised after independent review; human security sign-off pending | Date: 2026-11-07, updated 2026-11-08 | Decision owners: Security Architect, Database Architect, Compliance owner (named approvers TBD)

Source: Spec v3.2 IAM-FR-026, IAM-FR-027 ("acknowledged audit records are never lost"), IAM-WF-009, -020, §12.6 (sealed into a hash chain every minute), §12.10, K.5, OPEN-002; Blueprint v1.0 §6 (`audit` schema), §8; CLAUDE.md rule 9. Decision log DL-022, DL-023, DL-037. Drafted by Claude Code (S1-01); decisions taken by the repository owner (sivanandanr) on 2026-11-08; implemented in S1-07.

## Context and requirements
Audit every sensitive change, decision, policy/version snapshot, correlation ID and connector outcome without leaking secrets or sensitive attributes; tamper-evident.

## Options considered
1. Audit writer from `Audit.Contracts` invoked inside the caller's transaction (same DB, `audit` schema, `iam_audit_writer` permissions).
2. Business transaction writes outbox; Audit consumer persists asynchronously.
3. Dedicated audit outbox per module, relayed to the audit store.

## Decision and rationale
Owner decisions (2026-11-08):
- **Atomicity — option 1, same transaction (DL-022).** `IAuditWriter.AppendAsync(DbTransaction, AuditEventRecord, ct)` inserts on the caller's own open transaction, so the audit row and the business change commit or roll back together. A completed transaction is refused. **If anything fails** (rejected record, database error, timeout, cancellation), the writer rolls back the caller's transaction before rethrowing, so a caller that catches the exception cannot commit the change without its audit row.
- **Append-only — grants plus triggers**, two independent layers on both engines (below), each tested separately by its own error.
- **Integrity checksum — whole row** (revised after review; replaces "payload hash only"). `RowHash` = lower-case hex SHA-256 over every stored field except the database-assigned `Seq`. It is **unkeyed**: it detects accidental or partial edits and lets a reader verify a row, but anyone able to change a row can recompute it. It is **not** tamper-proofing. The keyed per-minute hash chain (spec §12.6) is **not implemented**.
- **Table name — `audit.AuditEvent`** (blueprint), one general table; approval events are a kind of AuditEvent. Spec K.5's `audit.ApprovalAuditEvent` is recorded as spec question DL-037.

Stored fields: `Id` (UUIDv7, app-generated), `Seq` (database identity, ordered, **not gap-free**, never supplied by writers), `RecordedAtUtc` (writer clock), `OccurredAtUtc`, `ActorId` (from the server-side actor accessor, never from the record), `EffectiveActorId`, `Action`, `TargetType`, `TargetId`, `Outcome`, `Reason`, `CorrelationId`, `CausationId`, `PolicyVersionRef`, `PolicyVersionHash`, `PayloadCanonical`, `RowHash`, `SchemaVersion`. Primary key `(RecordedAtUtc, Id)` to allow later PostgreSQL partitioning; unique indexes on `Id` and `Seq`. No `TenantId`: deployment per customer (DL-001, ADR-0014). Append-only tables get none of the S1-06 Created*/Modified*/Version columns (`IsAppendOnly()` convention; the save interceptor also refuses to update or delete them).

Row hash layout (`AuditEventValidator.ComputeRowHash`): SHA-256 of the UTF-8 compact JSON array `["iam.audit.row.v1", Id, RecordedAtUtc, OccurredAtUtc, ActorId, EffectiveActorId, Action, TargetType, TargetId, Outcome, Reason, CorrelationId, CausationId, PolicyVersionRef, PolicyVersionHash, PayloadCanonical, SchemaVersion]`; GUIDs lower-case "D"; timestamps UTC `yyyy-MM-ddTHH:mm:ss.ffffffZ`; absent values JSON null; default `System.Text.Json` escaping.

Validation before anything is written (`AuditEventValidator`):
- actions must be well-formed dotted codes **and registered** (`AuditActionRegistration`, checked once by the singleton `AuditActionCatalog`);
- every text field is bounded; control characters, Unicode format characters (bidi overrides, zero-width) and line/paragraph separators are refused; CR/LF allowed in `Reason` only;
- timestamps must be UTC and not more than 5 minutes in the future;
- payload ≤ 64 fields, values ≤ 2,000 characters, canonical form ≤ 16,384 characters; every payload field must be explicitly classified `Plain` or `Sensitive` (no default);
- field names suggesting secrets are refused whatever their class (matched with `.`/`_` removed: password, passphrase, secret, token, credential, api key, private/signing/account key, authorization, bearer, assertion, cookie, session id, connection string; and exact short names pin, otp, totp, hotp, cvv, sig);
- credential-shaped values are refused in **every** free-text field (payload values, Reason, TargetId, EffectiveActorId, PolicyVersionRef, ActorId): JWTs, `Bearer`/`Basic` + token anywhere, PEM blocks, `Password=`/`Pwd=`/`AccountKey=`/`SharedAccessKey=`, SAS `sig=`, AWS access key IDs;
- `Sensitive` values are replaced with `[masked]` **before** canonicalisation and hashing.
Rejection messages name the field and rule, never the value. Pattern matching is best effort, not a guarantee (R-027).

Append-only enforcement:

| Layer | SQL Server | PostgreSQL |
|---|---|---|
| Module runtime logins | Member of `iam_audit_writer`: `INSERT` on `audit.AuditEvent` only; `DENY UPDATE, DELETE, ALTER ON SCHEMA::audit` (so also no `TRUNCATE` or `IDENTITY_INSERT`) | Granted `iam_audit_writer WITH INHERIT TRUE, SET FALSE` (PostgreSQL 16+): `USAGE` on schema; `INSERT` on every column of `audit.audit_event` **except `seq`** |
| Audit module login | No schema-wide grant (bootstrap revokes any older one); member of `iam_audit_reader` (`SELECT` on `audit.AuditEvent` only) and `iam_audit_writer` | No table, sequence or default privileges (bootstrap revokes older ones); `iam_audit_reader` (`SELECT` on the table only) and `iam_audit_writer` |
| Migration history in `audit` | Migrator only | Migrator only |
| Trigger (binds the schema owner too) | `INSTEAD OF UPDATE, DELETE` raising error 51000 | `BEFORE UPDATE OR DELETE` (row) and `BEFORE TRUNCATE` (statement), `ENABLE ALWAYS`, raising SQLSTATE P0001 |

Grant-layer refusals are permission errors (SQL Server 229/230, PostgreSQL 42501); trigger refusals carry "append-only" — the tests tell them apart. The writer runs one constant, parameterised INSERT and never reads the row back, so writers need no `SELECT`.

## Security and privacy impact
Residual risks (not production-ready as tamper-proof — R-026, R-027):
- A database administrator, superuser, or the schema owner (migrator) can disable or drop the triggers and change rows; on SQL Server the owner can also `TRUNCATE`. The migrator is a deployment-time identity whose changes go through reviewed migrations. The real immutability controls are the keyed hash chain and SIEM/WORM export — both later.
- Any module login can bypass `IAuditWriter` with its own SQL `INSERT` and forge `ActorId`, `RecordedAtUtc`, `OccurredAtUtc` or unmasked values (it cannot choose `Seq`). Planned mitigation: a database-side insert procedure as the only insert path, and the keyed sealer.
- `EffectiveActorId` (delegation) is taken from the caller unchecked; `ActorId` is the bare token `sub` with no issuer; background workers have no actor yet, so their audit writes fail (fail closed).
- `OccurredAtUtc` has no lower bound, so backdating is possible.
- Secret detection is pattern-based and can miss secrets in unusual formats.

## SQL Server vs PostgreSQL impact
Grant and trigger syntax differ. SQL Server has no column-level `INSERT` permission; supplying `Seq` there needs `IDENTITY_INSERT` (ALTER), which is denied. Behaviour is identical except owner `TRUNCATE` on SQL Server. SQL Server ledger tables are not portable and conflict with retention purge — not used. PostgreSQL 16+ is required for the per-grant `INHERIT` option (module roles are `NOINHERIT`).

## Hybrid deployment impact
Audit export destinations (SIEM) to be decided.

## Operational consequences and rollback
Deployment order: `bootstrap-module.sql` for schema `audit` → `grant-audit-role.sql` with role `iam_audit_writer` once per module runtime login (including the audit login) and with `iam_audit_reader` for the audit login → migrations (the migration grants to both roles and fails if either is missing). Re-running the bootstrap revokes grants an earlier version gave the audit login. The audit migration's `Down` throws: the audit store is never dropped by migration. Rollback is a compensating forward migration; data recovery by restore/PITR (DL-020). Retention purge requires a separate approved identity and procedure respecting legal hold — not in Sprint 1 (DL-023).

## Tests / evidence
`tests/IAM.Persistence.Tests`:
- `Audit/AuditEventValidatorTests`: validation, masking, row-hash coverage and determinism, secret names and credential-shaped values in every free-text field, invisible characters.
- `ConventionTests`: append-only model; migrations match the model; the writer's INSERT names exactly the model's columns except `Seq`; invalid action registrations fail; the interceptor refuses update/delete of append-only rows.
- `Parity/AuditParityTests`, on both engines: migration apply/re-run; row commits and rolls back with the business change; a rejected record, a cancelled write and a missing actor each roll back the business change even when the caller then commits; committed or rolled-back (not disposed) transactions refused by the writer's own guard; stored row masked and recomputes to its `RowHash`; `Seq` increases; **grant layer** (permission errors only): module logins cannot read, update, delete or truncate, the audit login can read but not change, no runtime login can read or write the migration history, writers cannot supply `Seq`, PostgreSQL `SET ROLE` to the writer role refused; **trigger layer**: the schema owner cannot update or delete (or truncate on PostgreSQL).
- `IAM.Api.Tests`: the API resolves `IAuditWriter`.
Results and mutation checks are recorded in ADR-0010.

## Not implemented (S1-07)
- Keyed hash-chain sealing every minute (spec §12.6): planned cluster-singleton sealer under advisory lock writing `audit.EvidenceAnchor` (Seq range, batch digest, previous anchor hash, HMAC/signature with vault key id); must not assume gap-free `Seq`.
- Database-side insert procedure; SIEM/WORM export; retention and legal hold (DL-023); audit read API or screen; audit calls from feature modules (they arrive with their stories); actor model for background workers.

## Open questions and approvals
- Retention periods and legal-hold authority (OPEN-002; spec §12.10 gives periods while H says pending) — DL-023.
- K.5 `audit.ApprovalAuditEvent` vs blueprint `audit.AuditEvent` — DL-037 (spec owner).
- Human security sign-off. Independent AI reviews (security-architect, code-reviewer) found H1/H2/M1–M5 and code findings 1–3, all addressed in this revision; this does not replace human approval (CLAUDE.md: never self-approved).
