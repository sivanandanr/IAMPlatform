# Additional requirements

These add to the functional specification and blueprint named in `CLAUDE.md`. They don't replace them.

## Additional requirements (Proposed)

| ID | Requirement | Status | Decision |
|---|---|---|---|
| AR-1 | The IAM Portal is governed like any other application. Optional per deployment, off by default. | Proposed | D1, D2 |
| AR-2 | Optional application roles (User Admin, IT Admin and more) that workflows can use as approvers or doers. | Proposed | D3, D4 |
| AR-3 | Action-only levels can enter or change chosen names and identifiers, never scope. | Proposed | D5, D6 |

Full detail: see `additional-requirements-prompt.md`. Change Status to Approved or Rejected after review.

Where that file differs from `docs/architecture/DECISION_LOG.md` (DL-049 to DL-055), the decision log wins. For example, the file says "per tenant", and the switch is per deployment.

Nothing here is built yet. Build order and status: `TRACEABILITY_TEMPLATE.md`.
