# Runbook — Access control integrity failure (P0-013, DL-060, DL-061)

Status: **draft, not reviewed.** Written with stage 2 slices 1–2; nothing calls access control from the API yet (slice 3). Needs Security Architect and DBA lead review before any shared environment uses it.

## What it protects
The stored access model (schema `configuration`: roles, role actions, assignments, incompatible pairs) is checked on every read:
- every rule (`AccessModel.Load`): known actions, assignable roles, incompatible pairs, separated duties;
- the hash stored with the latest version (`AccessModelVersion.ModelHash`, `v1:` + SHA-256 of `AccessModel.ContentHash`);
- the link: the latest version is the bootstrap, or names a request approved as exactly that version.

If any check fails, the read fails with `AccessStoreIntegrityException` (`Check` = `rule`, `hash`, `link` or `change`) and an `administration.access.integrity_failed` audit event (outcome Failed, payload `step` and `check`). **Every access check then fails closed**: nobody can read the model, propose, approve or bootstrap until it is repaired. That is deliberate (CLAUDE.md rule 3).

## What it does not protect
The hash has no secret key (R-033). Someone with the module's database login can rewrite the tables **and** forge a matching version chain, and nothing will notice. A keyed hash waits for the vault (P0-013b, DL-061a). Until then, treat the module login and the migrator as able to grant roles, and restrict and monitor them.

## When it fires
1. Treat it as a security incident first: someone may have written to `configuration` outside the application.
2. Find the failing check: the audit event's `check` field, and the server log.
3. Do not "fix" it by updating the hash. That would hide the edit it detected.

## Finding what changed
- Every applied change is in the audit log: `administration.access.bootstrapped` (people named), `administration.access_change.approved` (the change, `modelVersion`, `modelHash`, and `PolicyVersionRef`/`PolicyVersionHash` of the model it was made against).
- Rebuild the expected model by replaying, from the bootstrap defaults (`AccessModel.Defaults`, at the code version that bootstrapped), each approved change in version order. Its hash must equal the audited `modelHash` at each step.
- Compare the rebuilt model with the tables. The difference is what was written outside the application.

## Repairing
Only with two people (one writes, one checks), using the migrator login, in one transaction:
1. Restore the current-state tables to the rebuilt model.
2. Confirm a read succeeds: the hash of the restored tables equals the latest version's hash.
3. Record what was found and done in the incident record. The audit log already holds the failures.

If the latest **version row itself** is wrong (`link` failure, or a forged version): versions are append-only and can't be removed by the application logins. That needs a reviewed migration, approved like any sensitive change. Do not disable triggers ad hoc.

## Releases that change the model format
These lock every deployment out unless they ship a reviewed migration that re-hashes (DL-061c):
- a change to `AccessModel.ContentHash` (bump `HashScheme` to `v2`, and change the `CK_AccessModelVersion_ModelHash_Hex` / `ck_access_model_version_model_hash_hex` CHECK in the same migration);
- removing an action id, or making an action "everyone", while a stored role holds it;
- adding a pair to `ActionCatalog.ConflictingActions` while someone holds both.
Test the upgrade against a copy of a real `configuration` schema before release.

## Not covered here
- Break-glass: it must never depend on this store (ADR-0008, separate ADR).
- Rate limiting of integrity audit events: needed before slice 3 reads the model on every request (R-033).
