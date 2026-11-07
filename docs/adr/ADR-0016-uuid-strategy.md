# ADR-0016: Identifier (UUID) strategy
Status: Proposed — blocking (S1-06) | Date: 2026-11-07 | Decision owners: Database Architect, Lead Solution Architect (named approvers TBD)

Source: Blueprint v1.0 §7 ("Guid/UUID identifiers with an agreed UUID generation strategy"). Drafted by Claude Code (S1-01); not approved.

## Context and requirements
IDs must be generated before insert (outbox, idempotency keys) and behave identically on both engines.

## Options considered
1. UUIDv7 generated in the application (`Guid.CreateVersion7()`), no DB defaults.
2. Database-generated (`NEWSEQUENTIALID()` / `gen_random_uuid()` / `uuidv7()` in PostgreSQL 18).
3. Integer identity keys with separate public UUIDs.

## Decision and rationale
Recommendation: option 1. Caveat: SQL Server `uniqueidentifier` sorts by bytes 10–15 first, so v7 values are **not** sequential in SQL Server indexes. For high-volume append tables on SQL Server use a NONCLUSTERED Guid primary key plus a clustered key on (RecordedAtUtc, Seq). Never infer business ordering from Id order.

## Security and privacy impact
Non-guessable IDs reduce enumeration but are not authorization; object-level checks still required. v7 leaks creation time — acceptable for internal IDs; confirm for any externally exposed ID.

## SQL Server vs PostgreSQL impact
Different index fragmentation behaviour; parity tests assert functional equivalence, not physical layout.

## Hybrid deployment impact
None.

## Operational consequences and rollback
Changing strategy later affects only new rows if IDs remain Guid-typed.

## Tests / evidence
Insert/read round-trip on both engines; ordering tests never rely on Id. Evidence: none yet.

## Open questions and approvals
- Confirm acceptability of creation-time leakage for IDs exposed in URLs.
