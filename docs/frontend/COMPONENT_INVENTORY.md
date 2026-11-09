# Initial shared UI component backlog

| Priority | Component | Core requirements |
|---|---|---|
| P0 | TextInput | CVA, label/hint/errors, max length, disabled, accessibility |
| P0 | Autocomplete | Async debounced search, cancellation, keyboard, bounded options |
| P0 | Select | Single select, options by stable ID, keyboard and clear |
| P0 | Toggle | Native switch/checkbox semantics, CVA |
| P0 | DatePicker | Locale, date-only/timezone, min/max, keyboard |
| P0 | FormField | Consistent labels, hints, errors, required state |
| P0 | Button / Dialog | Focus management, confirm action, disabled/loading |
| P0 | DataGrid (AG Grid wrapper) | Server-side operations, permission-aware actions, safe rendering |
| P1 | MultiSelect | Chips, selection limits, keyboard |
| P1 | DateRange | Inclusive/exclusive rules, timezone boundaries |
| P1 | TextArea / NumberInput | Validation and numeric formatting |
| P1 | Toast / Tooltip / Tabs | Accessible notifications and navigation |

Every component must pass security, accessibility and validation tests before release.

## Status (2026-11-08, after S1-10; pending independent review and the UI foundation gate)

| Component | Status |
|---|---|
| TextInput, Select (with clear), Toggle, FormField, Button / Dialog, TextArea, Toast | Built (S1-09, S1-10 follow-ups) |
| Autocomplete, DatePicker, DateRange (inclusive start and end, date-only values), Tooltip | Built (S1-10) |
| DataGrid (AG Grid wrapper) | Built: server-side paging, sorting, filters, server-side export (S1-09, S1-10). Permission-aware row actions are not built yet |
| MultiSelect, NumberInput, Tabs | **Not built** — P1, not required by the UI_FOUNDATION_FIRST control list; to be scheduled |

Details, evidence and open items: ADR-0019.
