# ADR-0015: Ownership of the attribute catalog (AttributeDefinition)
Status: Proposed — **blocking** (S1-08) | Date: 2026-11-07 | Decision owners: Lead Solution Architect, IAM Domain lead, Functional Lead (named approvers TBD)

Source: Spec v3.2 Appendix L.4 (`identity.AttributeDefinition`, `identity.AttributeValue / IdentitySnapshot`), IAM-WF-013, -014, -019; Blueprint v1.0 §6 ("may be placed under identity or a dedicated metadata schema … one authoritative owner"); TRACEABILITY_TEMPLATE.md (IAM-WF-013 listed under Policy/Workflow). Decision log DL-005, DL-007. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Policy and Workflow modules reference attributes by stable `AttributeDefinitionId`. Exactly one module owns the catalog; others consume it through Contracts.

## Options considered
1. Identity module owns `identity.AttributeDefinition` and `identity.IdentityAttributeValue` (matches spec L.4).
2. A dedicated Metadata module and `metadata` schema.
3. Policy module owns it (traceability template hint).

## Decision and rationale
**Not decided.** Recommendation: option 1 (follows L.4; values and provenance naturally live with identity data). Exposed via `Identity.Contracts`: catalog query interface + `AttributeDefinitionPublished` event.

Proposed storage (Database Architect): `AttributeDefinition` with child table `AttributeDefinitionOperator` (not JSON); `IdentityAttributeValue` with typed nullable columns (ValueString nvarchar/varchar(400) binary collation, ValueNumber decimal(28,8), ValueDate, ValueBool, ValueTimestamp), CHECK at most one non-null, `Ordinal` for multi-value, provenance columns (SourceSystemId, SourceRecordRef, SourceObservedAtUtc, SyncedAtUtc). Requires a minimal `identity.Identity` table (not yet specified).

## Security and privacy impact
Values are HR-derived PII; `Sensitivity` drives masking in audit/evidence. Changes to sensitivity/allowed operators are authorization-affecting → maker-checker (IAM-WF-019).

## SQL Server vs PostgreSQL impact
Binary collation for codes (`Latin1_General_100_BIN2` / `COLLATE "C"`); trailing-space CHECK on SQL Server; parity tests for "01" vs "1".

## Hybrid deployment impact
HR sync worker writes values through the Identity module's application layer, not directly to tables.

## Operational consequences and rollback
Moving ownership later requires schema moves on both engines.

## Tests / evidence
See S1-08 acceptance criteria. Evidence: none yet.

## Open questions and approvals
- Sprint 1 maker-checker for activation, or keep activation locked until maker-checker exists (DL-007)?
- Is an `AttributeDefinitionVersion` table required (IAM-WF-019 immutable versions)?
