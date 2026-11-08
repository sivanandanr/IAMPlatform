# Mandatory UI Foundation First Gate — IAM Platform

## Policy
Before developing feature screens (Employee, Application, Entitlement, Access Request, Approval), build and validate a reusable first-party Angular UI component library. Kendo UI and PrimeNG may inform UX patterns only; do not install, wrap, copy code/assets/styles from, or depend on those libraries. AG Grid is the sole preapproved third-party UI control, wrapped by `iam-data-grid`. Confirm licensing for Enterprise features. Other libraries require an explicit ADR and owner approval.

## Recommended package layout
Angular CLI workspace in `frontend/` (ADR-0019, DL-017): the library is `projects/iam-ui` (package `@iam/ui`, public API `src/public-api.ts`; folders `lib/controls/`, `lib/forms/`, `lib/overlays/`, `lib/feedback/`, `lib/tokens/`, `styles/`, `testing/`), the AG Grid wrapper is the secondary entry point `projects/iam-ui/grid` (`@iam/ui/grid`), and apps live beside it (`projects/iam-portal`). Do not allow feature code to import internal implementation files; import only the public entry points (enforced by ESLint). (Updated in S1-09; replaces the earlier `frontend/src/app/shared/ui/` layout.)

## Phase UI-0: tokens and foundations
Create design tokens for typography, spacing, color, density, focus, z-index, responsive breakpoints, dark/light theme and accessibility contrast. Define stable API naming, states (default, hover, focus, active, disabled, readonly, invalid, loading), error display and consistent icons. Set up Storybook or an in-repo component playground only with approval; no runtime third-party control dependency.

## Phase UI-1: first controls (required before feature screens)
1. `iam-textbox`: label, hint, required, maxLength, trim policy, input mode, autocomplete, error, prefix/suffix.
2. `iam-textarea`: limits, counters, auto-resize bounds.
3. `iam-checkbox`, `iam-radio-group`, `iam-toggle`: semantic input and keyboard behavior.
4. `iam-dropdown`: single select, optional search, async options, empty/loading/error states.
5. `iam-autocomplete`: debounced search, cancellation, server-side pagination, keyboard navigation, ARIA combobox semantics; never trust selected label as authorization.
6. `iam-date-picker`, `iam-date-range`: localization, timezone rules, min/max, invalid dates, keyboard access.
7. `iam-button`, `iam-icon-button`, `iam-form-field`, `iam-validation-summary`.
8. `iam-dialog`, `iam-confirm-dialog`, `iam-toast`, `iam-tooltip` with safe focus management.
9. `iam-data-grid`: AG Grid wrapper, typed columns, paging, sorting/filtering allowlists mapped to backend, accessible empty/loading/error states, server-side query contracts and export authorization.

## Security requirements
Never use `bypassSecurityTrust*`, unsafe `innerHTML`, dynamic `eval` or code from configuration. Avoid direct DOM manipulation. Render text as text, not HTML; sanitize and encode only in the appropriate output context. CSP/Trusted Types where feasible. UI authorization is presentation only; APIs enforce action and object-level authorization. Avoid tokens in localStorage/sessionStorage. Use secure auth architecture per approved ADR. For searchable controls, debounce, cancel stale requests, cap queries, do not disclose sensitive records through autocomplete. For grid, never forward arbitrary sort column, filter expression or export limits to the backend; server validates allowlists and caps. Display safe ProblemDetails validation errors.

## Accessibility and performance
Target WCAG 2.2 AA. All controls have programmatic names, described errors, visible focus, keyboard operation, correct ARIA, screen reader status announcements and reduced-motion behavior. Use Angular CDK only if explicitly approved as infrastructure (not as a prebuilt UI control). Prefer native controls and browser APIs where reliable. Avoid unbounded rendering; virtualization for large option sets; cancellation and bounded caching.

## Required automated evidence
Unit/component tests for all states and ControlValueAccessor behavior, accessibility checks (including keyboard and screen-reader-oriented assertions), visual regression baselines for key states, responsiveness tests, malicious text/XSS rendering tests, and contract tests for dropdown/autocomplete/grid against mocked backend ProblemDetails. CI must fail on regression.

## Hard gate before IAM feature screens
Frontend Architect must approve the public component APIs and tokens. Security Architect must approve dangerous rendering, auth and search/export surfaces. QA must provide passing test evidence. Lead Solution Architect records the UI library ADR. Feature developers must reuse shared components rather than creating local duplicates. Exceptions require an ADR, security review and explicit owner approval.

## Suggested first sprint
Day 1: tokens, form-field, textbox, button; Day 2: checkbox, radio, toggle, textarea; Day 3: dropdown and autocomplete; Day 4: date controls and dialog; Day 5: AG Grid wrapper, validation/error handling, accessibility/security review. This is an indicative order, not a delivery commitment.
