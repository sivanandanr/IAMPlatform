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
