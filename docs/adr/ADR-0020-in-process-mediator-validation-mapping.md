# ADR-0020: In-process dispatch, validation and mapping libraries
Status: Proposed — blocking (S1-02) | Date: 2026-11-07 | Decision owners: Lead Solution Architect; licence approval by procurement/legal (named approvers TBD)

Source: CLAUDE.md ("Ask before changing dependency versions or adding paid/licensed packages"); BACKEND_INPUT_VALIDATION.md. Decision log DL-018. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Vertical-slice commands/queries need dispatch, validation and DTO mapping. Several popular libraries changed to commercial licences in 2025 (MediatR, AutoMapper — **verify current terms before approval**).

## Options considered
- Dispatch: hand-written handlers invoked from endpoints / MediatR (commercial).
- Validation: built-in .NET validation + endpoint filters / FluentValidation (Apache-2.0 at time of review — verify).
- Mapping: manual mapping / Mapster (MIT — verify) / AutoMapper (commercial).

## Decision and rationale
**Not decided.** Recommendation: hand-written dispatch and manual mapping (no licence risk; explicit mass-assignment control); FluentValidation if approved, otherwise built-in validation.

## Security and privacy impact
Manual mapping makes field allowlists explicit (mass-assignment protection). Validation pipeline is mandatory on every endpoint (SEC-S1-11).

## SQL Server vs PostgreSQL impact
None.

## Hybrid deployment impact
None.

## Operational consequences and rollback
Swapping libraries later is localised if handlers stay framework-free.

## Tests / evidence
Validation negative tests per BACKEND_INPUT_VALIDATION.md. Evidence: none yet.

## Open questions and approvals
- Licence verification and approval for any third-party package chosen.
