# IAM Frontend Component Architecture — mandatory

## Non-negotiable dependency policy
- Build our own Angular standalone UI controls, styles, templates, keyboard interactions and validation display. No Kendo UI, PrimeNG, Angular Material, NG-ZORRO, Bootstrap component packages or other third-party control libraries without an explicit approved ADR.
- Kendo UI and PrimeNG may be consulted for UX patterns and behavior only. Do not copy proprietary source, styles, markup, icons or licensed assets.
- **AG Grid is the single approved third-party data-grid exception.** Wrap it in our own `IamDataGridComponent`; feature modules must not depend directly on AG Grid. Review AG Grid Community vs Enterprise features and license terms before enabling Enterprise-only functionality. Never commit license keys.
- Angular framework packages, Angular CDK accessibility/overlay primitives and non-UI infrastructure libraries may be proposed only through dependency review; Angular CDK must not be used to introduce prebuilt UI controls. Avoid dependency bloat.

## Component library location
`frontend/src/app/shared/ui/` with public exports through `index.ts`; feature modules import only the public surface. Maintain design tokens, component API contracts, examples and tests. Prefer standalone components, Angular Signals, OnPush, typed Reactive Forms, strict TypeScript, Tailwind utility classes plus scoped CSS. Use semantic HTML and native browser controls when they already meet requirements.

## Required reusable components — initial priority
1. `IamTextInputComponent`: label, placeholder, hint, required, readonly, disabled, maxLength, inputMode, autocomplete, error state, prefix/suffix, accessible label association.
2. `IamAutocompleteComponent`: debounced searchable text input; async lookup, cancellation/stale-response prevention, minChars, min/max results, loading/empty/error states, arrow/Enter/Escape behavior, active-descendant and selection semantics; server-side search for large datasets.
3. `IamSelectComponent`: single-select dropdown with keyboard support, searchable mode only when justified, option IDs distinct from labels, loading/empty states.
4. `IamMultiSelectComponent`: token/chip selections, max selected, clear/remove, keyboard navigation and bounded results.
5. `IamToggleComponent`: accessible native checkbox/switch semantics, label, disabled, pending state; never treat visual state as authorization.
6. `IamDatePickerComponent` and `IamDateRangeComponent`: locale/timezone-aware parsing, calendar keyboard navigation, min/max, date-only vs timestamp distinction, DST handling, clear state.
7. `IamCheckboxComponent`, `IamRadioGroupComponent`, `IamTextAreaComponent`, `IamNumberInputComponent`.
8. `IamButtonComponent`, `IamIconButtonComponent`, `IamDialogComponent`, `IamConfirmDialogComponent`, `IamToastComponent`, `IamTooltipComponent`, `IamTabsComponent`, `IamFormFieldComponent`.
9. `IamDataGridComponent` (AG Grid wrapper): server-side pagination/filter/sort, stable row IDs, virtual rendering where suitable, loading/empty/error states, column visibility, permission-controlled actions, accessible cells, exports subject to server authorization, never fetch millions of rows into browser.

## Shared component contract
- Support Angular ControlValueAccessor where used in forms, including `writeValue`, `registerOnChange`, `registerOnTouched`, `setDisabledState`; support typed reactive forms integration and `aria-describedby` for hints/errors.
- Use inputs/outputs or Signals consistently. Never mutate parent form state outside declared callbacks.
- All interactive controls: keyboard operability, visible focus, screen-reader names, logical tab order, WCAG 2.2 AA target, responsive layout, touch target and high contrast.
- Theme with central design tokens for color, spacing, typography, border, radius, z-index and motion. Support light/dark themes, reduced motion, RTL-readiness where practical.
- Avoid global DOM listeners without cleanup, memory leaks, `any` types, heavy change detection and large client-side option lists.
- All form controls show **server validation** errors. Frontend validation is convenience only; backend validation and authorization are authoritative.

## Security rules
- No unsafe `innerHTML`, bypassSecurityTrust* or arbitrary script execution. Escape interpolated content; treat option labels, grid cell content, HTML-like user data and API responses as untrusted. Do not render server-provided HTML unless explicitly sanitized through a reviewed design.
- Never accept request-supplied roles, approval statuses, permissions or ownership as authoritative. UI hiding is not authorization.
- No secrets, access tokens or sensitive HR data in localStorage/sessionStorage, logs or browser URLs. Prefer reviewed BFF HttpOnly/Secure/SameSite cookie architecture with CSRF defense; document any alternative.
- Sanitize/validate navigation destinations, file names and downloads; no arbitrary URLs in click handlers. Grid CSV/Excel export must mitigate formula injection and be authorized by backend.
- CSP and Trusted Types where feasible, strict CORS and secure response headers are backend responsibilities. Avoid dependencies requiring unsafe-inline or unsafe-eval.
- Debounce and cancel autocomplete HTTP calls; server enforces query length, rate limits, result limits and resource authorization.

## Component acceptance tests
- Unit/component tests: value, change/touched, disabled/readonly, required, server errors, reset, keyboard and focus, screen-reader semantics, light/dark, narrow viewport.
- Accessibility: automated axe checks plus manual keyboard/screen-reader spot checks.
- Autocomplete: empty, delayed, out-of-order, error, huge result, escape, selection, stale response.
- Date picker: invalid date, leap year, min/max, locale and timezone boundary.
- Grid: paging/filter/sort query contract, virtualization, unauthorized actions, malicious cell text, export formula injection, empty/error states.
- Screenshot/visual regression tests for shared controls before rolling out across feature modules.

## Architecture governance
- Frontend Architect owns shared component APIs and design tokens. Feature developers reuse existing controls; new controls require an approved component proposal and test plan.
- No third-party UI library added without ADR covering licensing, bundle impact, accessibility, security and maintenance. AG Grid upgrades require dependency/security review.
- Never duplicate custom controls per feature. Refactor into shared UI when behavior is general.
