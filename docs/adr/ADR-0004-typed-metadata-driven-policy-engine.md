# ADR-0004: Typed metadata-driven policy engine (attribute catalog + typed AST)
Status: Proposed | Date: 2026-11-07 | Decision owners: IAM Domain / Workflow lead, Security Architect, Business owners for thresholds (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 004), §9; Spec v3.2 IAM-WF-013, -014, -015, -016, -018, -022, Appendix L.1–L.6; CLAUDE.md rules 2 and 8. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Approval matrices and birthright policies must use a registered typed attribute catalog (not fixed Company/Department/EESubgroupCode columns). Adding a new mapped catalog attribute must not require a schema migration (IAM-WF-013; blueprint §16). Administrator-authored SQL, C#, JavaScript or arbitrary expressions must never execute (CLAUDE.md rule 8).

## Options considered
1. Registered `AttributeDefinition` catalog + typed JSON AST with an allowlisted operator set, evaluated in-process (proposal).
2. Expression language (e.g. dynamic LINQ, JS, CEL) evaluated at runtime.
3. Fixed columns per known attribute.

## Decision and rationale
Option 1.
- **AttributeDefinition** fields: stable Id, immutable Key, DisplayName, DataType (String/Code, Integer, Decimal, Date, Boolean), SubjectScope, AuthoritativeSource and priority, NormalizationRule, NullHandling, Sensitivity, Freshness, AllowedOperators, Status, Version, Maker/Checker.
- **AST nodes**: `group` (AND/OR, nested), `condition` (subject, attributeId, operator, values), explicit `wildcard` only where a rule allows it.
- **Operators**: String/Code → Eq, NotEq, In, NotIn, IsEmpty, IsNotEmpty (no ordering). Integer/Decimal/Date → those plus Gt, Gte, Lt, Lte. Boolean → Eq, IsEmpty, IsNotEmpty. No regex / StartsWith / Contains. New operators are engineering changes.
- **Literals** are JSON strings parsed against the attribute's DataType; no implicit coercion. `attributeId` is authoritative; `attributeKey` is a display label only.
- Unknown properties, duplicate JSON keys, unknown/inactive/unauthorized attributes, type-invalid operators and empty groups → 422.
- **Null semantics (proposal)**: any comparison on a missing or stale value (including NotEq/NotIn) is UNKNOWN; UNKNOWN reaching a deciding rule → HOLD. Missing data is never a wildcard and never falls through to a broader rule (IAM-WF-018).
- Published versions are compiled/cached; decision evidence includes policy version and hash (blueprint §9).

## Security and privacy impact
No code execution from configuration. Bounded complexity prevents evaluation DoS. Sensitive attribute values are minimised in evidence and audit (IAM-WF-020).

## SQL Server vs PostgreSQL impact
AST stored as JSON text (`nvarchar(max)` + `ISJSON` CHECK / `jsonb`) behind read adapters; query-critical metadata (attribute ids, rule priority, status) in indexed columns. Hashes computed over application-canonical bytes, never over a `jsonb` read-back (ADR-0017).

## Hybrid deployment impact
None.

## Operational consequences and rollback
Changing operator semantics requires a new AST `schemaVersion`; old published versions remain evaluable.

## Tests / evidence
EESubgroupCode registered with no migration; "01" vs "1" handled per DL-006; numeric literal for a Code attribute → 422; Gt on Code → 422; limits exceeded → 422; missing/stale → HOLD; replay of the same snapshot → same result. Evidence: none yet.

## Open questions and approvals
Complexity bounds are **undecided in the spec** (L.6). The following are PROPOSALS needing sign-off (DL-008): group depth ≤ 4; conditions per rule ≤ 32; IN/NOT IN values ≤ 256; literal ≤ 256 chars; AST ≤ 32 KB; rules per matrix version ≤ 1,000; evaluation time per item ≤ 250 ms → HOLD.
Also: EESubgroupCode normalization (DL-006), JobGrade data type (DL-012), null semantics (DL-009).
