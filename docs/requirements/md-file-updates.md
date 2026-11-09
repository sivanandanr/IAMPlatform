# md file updates for AR-1 to AR-3

Text to add to your project's md files. Each block says where it goes.

## 1. requirements.md

Add a new section at the end. Don't edit the existing requirements.

```markdown
## Additional requirements (Proposed)

| ID | Requirement | Status | Decision |
|---|---|---|---|
| AR-1 | The IAM Portal is governed like any other application. Optional per tenant, off by default. | Proposed | D1, D2 |
| AR-2 | Optional application roles (User Admin, IT Admin and more) that workflows can use as approvers or doers. | Proposed | D3, D4 |
| AR-3 | Action-only levels can enter or change chosen names and identifiers, never scope. | Proposed | D5, D6 |

Full detail: see additional-requirements-prompt.md. Change Status to Approved or Rejected after review.
```

## 2. CLAUDE.md

Claude reads CLAUDE.md in every session, so add only the short rules here. Keep the detail in requirements.md.

**Replace** this row in the Domain model table:

```markdown
| Application ownership | Application owner only. `BusinessOwnerId` stays nullable and isn't collected. |
```

**with:**

```markdown
| Application ownership | One required application owner. Optional application roles (User Admin, IT Admin, tenant-defined) come from a catalog, held by a person or group, each Optional or Required. A required role that is empty blocks requests. `BusinessOwnerId` stays nullable and isn't collected. |
```

**Add** these rows to the Domain model table:

```markdown
| Portal governance | Optional per tenant, switched on with a second person. The portal is an application with a built-in connector. Requestable portal roles need a basis. Privileged portal roles are just-in-time. The owner is outside the IAM team. The last IAM Administrator can't be removed. |
| Action-only fields | A workflow level may fill in or change listed names and identifiers only (account name, login, technical name, OU, notes). Never access, people, dates or privilege. Proposed and final values are audited. No password fields. |
| Manual work | Applications with no connector send manual work to the Ticket system or an IAM task. Details entered by a person stay "not verified" until discovery or a review confirms them. |
```

**Add** this line to Non-negotiable rules:

```markdown
- A person filling in details can change names, never what access is given or to whom.
```

## 3. README.md

- Add the new migrations and test files to the migration list and the test list, as the database steps require.
- Add a short "Optional features" note: AR-1 is off until a tenant turns it on. AR-2 and AR-3 do nothing until they are configured.

## 4. Decision log (docs/decisions.md, or create it)

Record D1 to D6 with the date, who decided, and the reason. When a decision changes, add a new entry instead of editing the old one, so the history stays clear.

```markdown
## 2026-11-09 Additional requirements AR-1 to AR-3
- D1 Portal owner is the CISO. Reason: keeps the IAM team from granting itself access.
- D2 Just-in-time admin session counts as the secondary portal account. Reason: same protection as a separate login, less admin.
- D3 User Admin and IT Admin can approve and do work, set per level.
- D4 An empty required application role blocks requests. Reason: closed by default.
- D5 Applications with no connector offer Ticket system or IAM task.
- D6 Changing a name doesn't trigger re-approval. Reason: only names can change.
```

## 5. iam-ui-guidelines skill (if you keep one)

Add one rule under Forms:

```markdown
- Where a step can change a proposed value, show the proposed value and the new value side by side, and ask for a reason.
```
