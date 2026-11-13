# Runbook — First access-control setup (P0-013 slice 5, DL-059e, DL-062c, DL-063a)

Status: **draft, not reviewed.** Needs Security Architect review before any shared environment uses it.

## What it does
Names the first **IAM Administrator** (proposes access changes) and the first **IAM Manager** (approves them), and stores the default roles. After that, every change goes through the admin API with a second person approving. It runs once per deployment: it is refused as soon as anything is stored, and the refusal is audited.

It is a command-line tool with no HTTP surface (`backend/src/Tools/IAM.Tools.AccessBootstrap`). Nothing on the API can do this.

## Who is involved
- **Operator:** runs the tool.
- **Database administrator:** creates the short-lived bootstrap login before, and removes it after.
- **Second person** (not the operator): confirms the result afterwards.
- **Approver of the change ticket:** the approval reference the tool records.

The two people named are agreed with the Security Architect beforehand. The tool can't check that a real second person agreed; the approval reference and the second person's confirmation are the controls.

## Before you start
- The `configuration` schema is migrated (`AccessControl` migration) and `grant-audit-role.sql` has run (so `iam_audit_writer` exists).
- You have the two people's identity-provider subject ids (`sub`), exactly as tokens carry them. Subjects are exact and case-sensitive: a typo assigns the role to nobody (R-033).
- You have the approval or ticket reference (3-64 characters: letters, digits, `.`, `-`, `_`, `:`, `/`).

## 1. Create the bootstrap login (database administrator)
Use a login name starting with `iam_bootstrap` and a password generated for this run only. Run `infra/db/<engine>/grant-access-bootstrap.sql` with the inputs bound as parameters (as for `bootstrap-module.sql`). It can read the `configuration` schema, insert the bootstrap rows and append audit events: nothing else. On PostgreSQL it expires after two hours; on SQL Server it stays until step 4.

The tool refuses any login that doesn't start with `iam_bootstrap`, so it can't be run with the API's own login.

## 2. Run it (operator)
Secrets come from the environment, never the command line or shell history:
```bash
export IAM_DB_PROVIDER=PostgreSql            # or SqlServer
read -rs ConnectionStrings__bootstrap && export ConnectionStrings__bootstrap
dotnet run --project backend/src/Tools/IAM.Tools.AccessBootstrap -- \
  --administrator '<sub of the first IAM Administrator>' \
  --manager '<sub of the first IAM Manager>' \
  --operator '<your short name, a-z 0-9 . - _>' \
  --approval '<ticket reference>' \
  --confirm
unset ConnectionStrings__bootstrap
```
Exit codes: `0` done; `2` refused (for example `AlreadyBootstrapped`, `SameBootstrapPerson`, `InvalidSubject`); `1` anything else (arguments, configuration, the wrong login, database). Output names a refusal code or an exception type only. Ctrl+C cancels; check the audit log before running again.

## 3. Confirm (second person)
- The audit log has `administration.access.bootstrapped` with actor `system:bootstrap:<operator>`, the two subjects, the approval reference, the operating-system user and host, and the model version and hash. Alert on this event: it should happen once per deployment.
- Each named person checks `GET /api/administration/permissions/me`: the administrator sees `iam-administrator`, the manager `iam-manager`.
- Record the confirmation against the approval reference.

## 4. Remove the bootstrap login (database administrator)
Run `infra/db/<engine>/revoke-access-bootstrap.sql` with the same login name. Do this even on PostgreSQL, where it would expire anyway.

## If it was run with the wrong people
It can't be run again. Use the admin API: the administrator proposes the correction and the manager approves it (for example assign the right person, then remove the wrong one). If the wrong person holds a role and the right people can't act, treat it as an incident and follow `ACCESS_CONTROL_INTEGRITY.md` (two people, reviewed migration); do not edit the tables.

## Known limits
- The success path (a fresh store) is covered by the service's own parity tests, not by an end-to-end run of the tool: the shared parity database is already set up (R-033).
- Whoever can create database logins can still run this first. The approval reference, the alert and the second-person confirmation make that visible; they don't prevent it.
