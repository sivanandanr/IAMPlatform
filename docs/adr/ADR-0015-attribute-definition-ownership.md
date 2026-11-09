# ADR-0015: Ownership of the attribute catalog (AttributeDefinition)
Status: Accepted (owner decision, 2026-11-08) — implemented in S1-08 and revised after independent review; human sign-off pending | Date: 2026-11-07, updated 2026-11-08 | Decision owners: Lead Solution Architect, IAM Domain lead, Functional Lead (named approvers TBD)

Source: Spec v3.2 Appendix L.1, L.4 (`identity.AttributeDefinition`, `identity.AttributeValue / IdentitySnapshot`), IAM-WF-013, -014, -015, -019; Blueprint v1.0 §6 ("may be placed under identity or a dedicated metadata schema … one authoritative owner"), §9, P0-007 ("EESubgroupCode registered, validated and queried"); TRACEABILITY_TEMPLATE.md. Decision log DL-005, DL-006, DL-007, DL-038. Drafted by Claude Code (S1-01); decisions taken by the repository owner (sivanandanr) on 2026-11-08.

## Context and requirements
Policy and Workflow modules reference attributes by stable `AttributeDefinitionId`. Exactly one module owns the catalog; others consume it through Contracts. IAM-WF-014: every attribute has an immutable key, display name, data type, authoritative source, subject scope, normalization rules, sensitivity, freshness requirement, permitted operators and status; unknown, inactive, untrusted or unauthorized attributes cannot be published in rules; leading-zero codes are preserved.

## Options considered
1. Identity module owns `identity.AttributeDefinition` (matches spec L.4).
2. A dedicated Metadata module and `metadata` schema.
3. Policy module owns it (traceability template hint).

## Decision and rationale
Owner decisions (2026-11-08):
- **DL-005 — option 1, Identity module, `identity` schema.** Other modules read it only through `Identity.Contracts.IAttributeCatalog`.
- **DL-006 — codes are exact strings.** "01" and "1" are different; no zero-stripping or numeric coercion anywhere. Each attribute declares its own normalization (`None` by default, `Trim`, `TrimUpperInvariant`; codes and text only).
- **DL-007 — activation locked, in the application and in the database.** S1-08 stores drafts only; publishing is refused (and the refusal audited). The database allows only `State = 'Draft'` (CHECK constraint), so no login, including the Identity app login with schema-wide DML, can publish by SQL. A reviewed migration lifts the constraint together with the maker-checker workflow.
- **Versions now.** `AttributeDefinitionVersion` from the start; published and retired versions are immutable (IAM-WF-019).

Implemented model (S1-08):

| Table | Purpose |
|---|---|
| `identity.AttributeDefinition` | `Id` (UUIDv7), `Key` (lower-case snake_case, 2–64 chars, binary collation, unique, **immutable**) |
| `identity.AttributeDefinitionVersion` | `VersionNumber` (unique per definition), `State` (`Draft` only until maker-checker; at most one `Draft` and one `Published` per attribute, filtered unique indexes), `DisplayName`, `DataType`, `SubjectScope`, `SourceSystem`, `Sensitivity`, `Normalization`, `FreshnessMaxAgeMinutes` (1–525,600), `MaxLength` (codes/text only, 1–400), `ContentHash` |
| `identity.AttributeDefinitionVersionOperator` | One row per allowed operator (not JSON) |

**Text attributes can be stored but cannot be used in rules** (DL-040, 2026-11-09). Codes with `TrimUpperInvariant` must be ASCII before upper-casing, so U+017F cannot become "S" (S2-01 review).

Enum-like columns are stored by name with binary collation. Data types: `Code`, `Text`, `Integer`, `Decimal`, `Date`, `Boolean`, `Timestamp`. Operators per type: codes and text — `Equals`, `NotEquals`, `In`, `NotIn`, `IsEmpty`, `IsNotEmpty`; numbers, dates and timestamps — those plus `GreaterThan`, `GreaterThanOrEqual`, `LessThan`, `LessThanOrEqual`; booleans — `Equals`, `NotEquals`, `IsEmpty`, `IsNotEmpty`. `ContentHash` = lower-case hex SHA-256 of the UTF-8 compact JSON array `["iam.identity.attribute.v1", key, versionNumber, displayName, dataType, subjectScope, sourceSystem, sensitivity, normalization, freshness, maxLength, [operators sorted by ordinal name]]`, enum values by name, strings escaped as `System.Text.Encodings.Web.JavaScriptEncoder.Default` does (non-ASCII and `< > & ' " + ` + "`" + ` as `\uXXXX` with upper-case hex). It covers content only, not `State` or `AttributeDefinitionId` (the key is unique and immutable). Two fixed expected values are tested and were reproduced independently outside .NET, so rules can cite exactly which definition they used.

Value checking (`AttributeValueValidator`): refuses raw input over 1,600 characters, applies only the declared normalization, then parses strictly by type (codes: 1–MaxLength of letters, digits, `_ - . /`; text: 1–MaxLength, never empty, so a missing source value stays absent; integers without separators; decimals ≤ 20.8 digits with one canonical text — trailing zeros removed, zero is `0`; `yyyy-MM-dd` dates; `true`/`false`; UTC timestamps in exact `…Z` formats). Every pattern is anchored with `\z`, so a trailing line break is never accepted. Display names must be NFC-normalized with no control, invisible, private-use or unassigned characters. Errors describe the rule, never the value.

Administration (`IAttributeCatalogAdministration`, Identity.Infrastructure): register (only a unique-key violation is reported as "already registered"; other database errors pass through), update draft (optimistic concurrency on the version row; a concurrent edit surfacing as a stale version or a duplicate operator row is reported as a conflict), request publish (refused with "no draft" when there is none; otherwise always refused and audited `Denied`), find draft. Each change is written with its audit event in one transaction (ADR-0018; actions `identity.attribute.registered`, `.draft_updated`, `.publish_refused`). Requests are narrow DTOs; enum values must be exact defined names (numbers and other spellings refused); no server-owned field can be supplied.

**No HTTP endpoints in S1-08:** catalog administration is a privileged action and waits for the permission model (R-023, ADR-0008, P0-013).

Database-level controls (hand-written in the `AttributeCatalog` migrations, both engines): `State = 'Draft'` only (DL-007); one draft and one published version per attribute; the definition's key and id cannot change; a version's id, definition and number cannot change; a non-draft version cannot be updated or deleted; operators of a non-draft version cannot be inserted, changed or deleted (the operator trigger reads the parent with `FOR SHARE` / `READCOMMITTEDLOCK`). The triggers bind the schema owner too (PostgreSQL triggers are `ENABLE ALWAYS`). Until maker-checker lifts the Draft-only constraint, the non-draft triggers are a second layer; the tests prove them by lifting the constraint as the schema owner for one test.

## Security and privacy impact
Catalog changes are authorization-affecting (IAM-WF-019), hence locked activation and audited changes. Sensitivity drives masking in audit/evidence once values exist. Residual risks: a database administrator or the schema owner can drop the constraint or disable the triggers (as for audit, R-026), and `TRUNCATE` needs owner rights the app login lacks; the Identity app login can write its own migration history, which could make a future tightening migration be skipped (R-024, raised); the reader does not yet re-verify `ContentHash` on read (planned with publishing); retiring a published version will need a reviewed trigger change.

## SQL Server vs PostgreSQL impact
Binary collation for codes (`Latin1_General_100_BIN2` / `"C"`); trailing-space CHECKs; snake_case on PostgreSQL. SQL Server triggers are declared to EF Core (`HasTrigger`) so it does not use `OUTPUT` without `INTO`; the snake_case convention moves declared triggers with the renamed table. Parity tests cover both.

## Hybrid deployment impact
HR sync worker will write values through the Identity module's application layer, not directly to tables (later).

## Operational consequences and rollback
Moving ownership later requires schema moves on both engines. Rollback: compensating forward migration (DL-020).

## Tests / evidence
`tests/IAM.Persistence.Tests`: `Identity/AttributeCatalogDomainTests` (keys, field rules, operators per type, content hash, "01" ≠ "1", strict typed values, exact enum names in requests); `Parity/AttributeCatalogParityTests` on both engines (register + audit + read back; duplicate keys refused by pre-check and by the unique index; failed audit write leaves no registration; optimistic concurrency; publish refused and audited; drafts invisible to `IAttributeCatalog`; key immutable via SQL; published versions and their operators immutable for the app login and the schema owner; unknown `State` refused; a published version is returned by the reader). Results in ADR-0010.

## Not implemented (S1-08)
- Publishing/activation and maker-checker (DL-007); IAM-WF-013's acceptance ("publish and resolve without a schema migration") therefore waits for it.
- Attribute values, `IdentitySnapshot` and `identity.Identity` (L.4); HR sync; freshness evaluation.
- Retirement transition (needs a trigger change), new draft versions after publishing, `AttributeDefinitionPublished` event, hash re-verification on read.
- HTTP endpoints and UI (permission model first).

## Open questions and approvals
- DL-038: spec L.1/L.3 write `EESubgroupCode IN (1,2,3,4,5,6)` as numbers; the blueprint writes `["1",…]`. Implemented as string codes (DL-006); spec owner to align L.1/L.3.
- Independent security review and human sign-off.
