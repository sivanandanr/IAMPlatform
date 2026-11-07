# ADR-0019: First-party Angular UI library and AG Grid wrapper
Status: Proposed — **blocking** (S1-09) | Date: 2026-11-07 | Decision owners: Frontend Architect (author — cannot self-approve), independent frontend reviewer / Lead Solution Architect, Security Architect, QA lead (named approvers TBD)

Source: CLAUDE.md "P0 frontend component policy" and "Mandatory UI foundation first"; docs/frontend/COMPONENT_ARCHITECTURE.md, COMPONENT_INVENTORY.md, UI_FOUNDATION_FIRST.md; Blueprint v1.0 §3 ("Angular 20+; TypeScript; Tailwind CSS"), §5; Spec v3.2 "Screen and design standards" ("Azure portal layout with Clarity styling"). Decision log DL-017, DL-018, DL-033. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
All common controls built in-house; Kendo UI / PrimeNG reference only; AG Grid is the sole approved third-party grid, wrapped in `IamDataGridComponent`. Feature screens blocked until the foundation is approved by Frontend Architect, QA and Security Architect.

## Options considered
- **Layout A** (matches docs): single app, library at `frontend/src/app/shared/ui/` behind an `@iam/ui` path alias, deep imports blocked by ESLint.
- **Layout B** (Frontend Architect recommendation): Angular CLI workspace with `frontend/projects/iam-ui` (library, `public-api.ts`) and `frontend/projects/iam-portal` (app). Requires doc updates.
- Angular version: pin newest stable major in active support at implementation time (v20 LTS ends around now) + matching Node LTS; zoneless + Signals.
- Styling: design tokens as CSS custom properties (source of truth); Tailwind theme maps onto them (COMPONENT_ARCHITECTURE already allows Tailwind utilities + scoped CSS).
- Angular CDK: allowed as infrastructure (a11y, overlay) or avoided in favour of native `<dialog>` + Popover API.
- AG Grid: Community (MIT) vs Enterprise (commercial; server-side row model, Excel export, set filters).

## Decision and rationale
**Not decided.** Proposed Sprint 1 scope (UI-0): tokens; form-field; button/icon-button; textbox; textarea; checkbox/radio-group/toggle; status-badge; dropdown; dialog/confirm-dialog; toast/validation-summary; read-only data-grid wrapper (Community, client-side row model with server paging, typed columns, plain-text cells, no export). Deferred to Sprint 2: autocomplete, date controls, multi-select, number input, tooltip, tabs, stepper, filter bar, dirty-form guard, grid filters/CSV export with formula-injection guarding. "Clarity styling" treated as visual reference only — no `@clr/*` package or copied CSS (needs confirmation).

## Security and privacy impact
No `[innerHTML]`, `bypassSecurityTrust*`, `eval`; plain-text grid cells; CSP nonce passed to Angular and AG Grid; no tokens in browser storage (ADR-0012); ProblemDetails rendered as text.

## SQL Server vs PostgreSQL impact
None.

## Hybrid deployment impact
None.

## Operational consequences and rollback
UI foundation is not in the blueprint backlog: the gate cannot close before end of Sprint 2 → Phase 0 likely extends (~6 weeks) or the P0-018 demo is API-level (DL-028). AG Grid Enterprise licence key, if bought, comes from deployment config — never committed.

## Tests / evidence
CVA tests, keyboard/focus, axe (zero serious/critical), token contrast, light/dark/phone visual baselines, XSS payloads render as text, lint bans forbidden packages and deep `ag-grid` imports. Evidence: none yet.

## Open questions and approvals
- Layout A vs B; Angular pin; Tailwind major; CDK yes/no; AG Grid edition; Clarity meaning (DL-017).
- COMPONENT_INVENTORY vs UI_FOUNDATION_FIRST priority/naming mismatch (DL-033).
- Spec "`*` shows everything" in search vs bounded, authorization-scoped results (DL-027).
