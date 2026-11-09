# ADR-0019: First-party Angular UI library and AG Grid wrapper
Status: Accepted (owner decision, 2026-11-08) — S1-09 implements the core set, revised after independent frontend-architecture and security reviews; the UI foundation gate stays **closed** until S1-10 and the Frontend Architect, QA and Security Architect approvals | Date: 2026-11-07, updated 2026-11-08 | Decision owners: Frontend Architect (cannot self-approve), independent frontend reviewer / Lead Solution Architect, Security Architect, QA lead (named approvers TBD)

Source: CLAUDE.md "P0 frontend component policy" and "Mandatory UI foundation first"; docs/frontend/COMPONENT_ARCHITECTURE.md, COMPONENT_INVENTORY.md, UI_FOUNDATION_FIRST.md; Blueprint v1.0 §3 ("Angular 20+; TypeScript; Tailwind CSS"), §5; Spec v3.2 "Screen and design standards" ("Azure portal layout with Clarity styling"). Decision log DL-017, DL-018, DL-033. Drafted by Claude Code (S1-01); decisions by the repository owner (sivanandanr), 2026-11-08.

## Context and requirements
All common controls built in-house; Kendo UI / PrimeNG reference only; AG Grid is the sole approved third-party grid, wrapped by the library. Feature screens are blocked until the foundation is approved by Frontend Architect, QA and Security Architect.

## Options considered
- **Layout A:** single app, library at `frontend/src/app/shared/ui/` behind an alias, deep imports blocked by lint only.
- **Layout B:** Angular CLI workspace with a library project and an app project.
- AG Grid Community (MIT) vs Enterprise (commercial).
- Angular CDK as infrastructure vs native `<dialog>`/Popover API only.

## Decision and rationale
Owner decisions (DL-017, 2026-11-08):
- **Layout B.** `frontend/` is an Angular CLI workspace:
  - `projects/iam-ui` — the library, package `@iam/ui`, primary entry `src/public-api.ts`;
  - `projects/iam-ui/grid` — secondary entry `@iam/ui/grid`, the only code that imports AG Grid (keeps AG Grid out of every bundle that shows no grid: first load 435 kB instead of 1.18 MB);
  - `projects/iam-portal` — the app; in S1-09 a component playground, not an IAM feature screen.
  The portal maps `@iam/ui` / `@iam/ui/grid` to the public API files only; ESLint refuses deep or relative imports into the library.
- **Angular 22.2** (newest stable, zoneless, Signals), **Node 24 LTS** (`frontend/.nvmrc`), TypeScript strict with strict templates; exact versions in `package.json` and `package-lock.json`.
- **AG Grid Community 36.3 (MIT).** No Enterprise features; no licence key.
- **Angular CDK 22.2 as infrastructure only:** overlay positioning (dropdown) and Dialog (focus trap, `aria-modal`, Escape, focus return). Every visible control is ours.
- **Tailwind 4.3** through PostCSS for page layout; its theme maps onto the design tokens. Library components use only the tokens.
- **Clarity is a visual reference only** — no `@clr/*` package and no copied CSS (ESLint refuses `@clr/*`).
- **Scope (DL-033):** core set in S1-09, the rest in S1-10 (below). Component names follow UI_FOUNDATION_FIRST (`iam-textbox`, `iam-dropdown`, …).

Implemented in S1-09:

| Area | Components / pieces |
|---|---|
| Tokens | `styles/tokens.css`: colour (light and dark, OS preference or `data-theme`), type, spacing, density, focus ring, layering, motion (off under reduced motion), breakpoints |
| Form plumbing | `iam-form-field` (label, required marker, hint, client and server errors), shared ControlValueAccessor base (Signals, NgControl binding, aria-invalid / aria-describedby / aria-required) |
| Controls | `button[iamButton]` (type="button" default; `loading` blocks clicks in the capture phase), `button[iamIconButton]` (required accessible name), `iam-textbox` (required maxLength, trim policy that never strips inner text or leading zeros), `iam-textarea` (counter, bounded growth), `iam-checkbox`, `iam-toggle` (role="switch"), `iam-radio-group` (fieldset, role="radiogroup"), `iam-dropdown` (WAI-ARIA select-only combobox; keyboard, type-ahead, optional search, debounced search capped at 100 characters, loading/error/empty states) |
| Overlays | `IamDialogService` + `iam-dialog`, `IamConfirmService` (alertdialog and Cancel-first focus for destructive actions; resolves true only on explicit confirm) |
| Feedback | `IamToastService` + `iam-toast-outlet` (polite for info/success, assertive for warning/error; max 5; errors stay until dismissed), `iam-validation-summary` (focusable alert; focus links only for safe ids), `readProblemDetails` (strict, bounded RFC 9457 reader) |
| Grid | `@iam/ui/grid` `iam-data-grid`: read-only, server-side paging and sorting; emits only allowlisted page sizes, sortable columns and in-range pages; the grid never reorders rows; plain-text cells (no renderer hook); CSP `styleNonce` from Angular `CSP_NONCE`; loading, error and empty states announced |

## Security and privacy impact
- **Dependency allowlist (complete control):** `scripts/check-dependencies.mjs` fails CI if `package.json` gains any package not approved in DL-018, or any version range; `.npmrc` sets `ignore-scripts=true` and `save-exact=true` for local installs too.
- **ESLint gates (guard rail, not a complete control):** HTML sinks (`[innerHTML]`/`[outerHTML]`/`[srcdoc]` in any case and `bind-`/`attr.` form, host bindings and `@HostBinding`, `innerHTML`/`outerHTML`/`srcdoc` by name or bracket access, `Renderer2.setProperty`/`setAttribute` with HTML or `on*` names, `insertAdjacentHTML`, `DOMParser.parseFromString`, `createContextualFragment`, any `.write()`), code execution (`eval` incl. `window.eval`, `Function` with or without `new`, string `setTimeout`/`setInterval`, `require`, non-literal or package `import()`), `bypassSecurityTrust*` (incl. bracket access), browser storage (`localStorage`, `sessionStorage`, `indexedDB`, `caches`, `document.cookie`, also through `window`/`globalThis`/`self`), other UI libraries, AG Grid outside `@iam/ui/grid`, Angular CDK in feature code, and deep or relative imports into the library. `scripts/lint-selftest.mjs` proves each gate fires: 51 checks, including the bypass shapes found in review. Static rules can still be evaded by indirection (aliasing a method, computed names); code review remains part of the control.
- Everything user- or server-supplied renders as text; tests feed an HTML/script payload through labels, errors, options, dialogs, toasts, summaries and grid cells.
- The dropdown's selected label and the grid's sort/page values are presentation only; the API must validate and authorize again (BACKEND_INPUT_VALIDATION).
- No tokens or personal data in browser storage; the theme choice is held in memory.
- npm install scripts are not needed: CI installs with `npm ci --ignore-scripts`.
- **CSP:** the AG Grid wrapper passes Angular's `CSP_NONCE` to AG Grid (`styleNonce`), but **no nonce is provided yet**: the portal has no `CSP_NONCE` provider or `ngCspNonce`, so as built it needs `style-src 'unsafe-inline'` for component styles and AG Grid's theme. Critical-CSS inlining is turned off in the production build so no inline `onload` handler is emitted. The nonce becomes active when the hosting BFF sets a per-response nonce (S1-10 or the BFF hosting story); a strict-CSP end-to-end check is still to do.
- Server messages and toasts are cleaned to single-line plain text (control characters replaced; bidi overrides and zero-width characters removed; cut without splitting surrogate pairs); the ProblemDetails field map has no prototype.

## SQL Server vs PostgreSQL impact
None.

## Hybrid deployment impact
None.

## Operational consequences and rollback
The UI foundation is not in the blueprint backlog: the gate closes after S1-10 at the earliest, so Phase 0 likely extends or the P0-018 demo is API-level (DL-028). Rollback: remove `frontend/` and the CI job; nothing else depends on it yet.

## Independent review (2026-11-08)
Frontend-architecture and security reviews (AI agents, advisory; human approval still required) found, and S1-09 fixed: lint bypasses (above); the validation summary never actually hid (`:host` display overrode `hidden`; it now renders no content at all when there is nothing to report); focus calls ran before content rendered under zoneless change detection (searchable dropdown unreachable by keyboard; summary focus) — now `afterNextRender`; every dropdown trigger looked disabled (`:read-only` matched the `div`); the active option did not scroll into view; grid right-alignment and the column-state API were silent no-ops without their AG Grid modules (now registered, with AG Grid's validation module in development and tests, and tests fail on AG Grid console warnings); the page-size selector showed the wrong size; out-of-range pages gave nonsense ranges; sorting was possible while loading; the stale grid API was kept; control ids could not be set (now `controlId`, validated); `required` was declared separately from the validator (now derived from it unless overridden); the form-control subscription did not follow a swapped control; the checkbox dropped field error ids when it had a hint; dropdown closing did not mark the field touched, Tab from the search box left focus in the overlay, a pending search was not cancelled, and a value missing from the options showed the placeholder; confirmations lacked `aria-describedby`; dialogs focused the close button first and had no `disableClose`; the backdrop style lived in the app; error toasts could be pushed out by the cap; the playground showed raw API field names.

Deferred to S1-10: heading-level inputs (dialog, summary and grid use `h2`), a single source for disabled state (the `disabled` input beside the form's disabled state), checkbox errors outside a form field, toast pause on hover/focus (WCAG 2.2.1), dropdown clear action, raw-control lint for feature templates, Playwright real-browser keyboard, focus-trap and visual tests, forced-colors mode, SSR-safe ids, and test isolation (a failing overlay test can leave an overlay that makes later tests in the same run fail).

## Tests / evidence
- Library (Vitest, jsdom): 84 tests — values, touched, disabled, required, reset, markAllAsTouched, swapped controls, server errors, keyboard and focus (including render-timed focus under automatic change detection), ARIA roles/names, axe-core WCAG 2.0–2.2 A/AA with zero moderate, serious or critical findings, injected HTML shown as text, token contrast recomputed for both themes (text 4.5:1, borders and focus 3:1), grid cells rendered by AG Grid as text, sort/page allowlists.
- Portal: 4 tests (shell, theme without browser storage, client and ProblemDetails errors mapped to the form's labels and linked to fields).
- Dependency allowlist; ESLint gates + 51-check self-test; Prettier; production build with size budgets; `npm audit --omit=dev --audit-level=moderate` (runtime) and `npm audit --audit-level=high` (all; the 14 moderate dev-tooling findings are one minimatch advisory, R-028).
- Mutation checks: undoing review fixes each fails its tests — summary rendered without problems (1), search focus back to a microtask (1), AG Grid cell-style module unregistered (6, through the AG Grid warning check), page-size selection removed (1), close no longer marking touched (the 2 touched tests, plus 6 unrelated tests through a leaked overlay — see below). A CSS-only check of the hidden summary was first found not to detect the bug in jsdom and was replaced.
- CI: `frontend` job in `.github/workflows/pr.yml`. Results recorded in the S1-09 completion report.

## Not implemented (S1-10 and later)
- `iam-autocomplete`, `iam-date-picker`, `iam-date-range`, `iam-tooltip`.
- Grid filters and CSV export with formula-injection protection; export authorization.
- Visual regression baselines and real-browser keyboard/screen-reader checks (needs Playwright — a new dependency, to be proposed in S1-10).
- RTL review; Trusted Types; CSP configuration of the hosting BFF.

## Open questions and approvals
- Frontend Architect, QA and Security Architect approval of the public component APIs and tokens (gate).
- DL-027: spec "`*` shows everything" in search vs bounded, authorization-scoped results (affects autocomplete, S1-10).
