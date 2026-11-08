# Review, Handoff and Approval Policy (P0-016)

Status: Proposed | Date: 2026-11-07 | Owners: Lead Solution Architect, Security Architect, QA lead (named approvers TBD)

Source: Blueprint v1.0 §13 (agent structure and governance), P0-016; CLAUDE.md task workflow and "never self-approve"; README.md security boundaries; docs/quality/DEFINITION_OF_DONE.md. Drafted by Claude Code; not approved.

## 1. Principles
1. **Agents advise and generate; humans approve.** No agent (Claude Code or any subagent in `.claude/agents/`) may approve, accept or sign off an ADR, a sensitive change, a release gate or a production action.
2. **Independence.** The author of a change never reviews or approves it — this applies to people and to agents. An agent that authored a change cannot be its reviewer, even under a different role name.
3. **Evidence, not claims.** Every review and completion report cites actual commands and actual output. "Not run", "skipped" and "blocked" are stated explicitly. Mocks are labelled as mocks (CLAUDE.md rule 10).
4. **Platform enforcement over configuration.** `.claude/settings.json` deny rules are best-effort only (see RISK_REGISTER R-007). Enforcement comes from branch protection, CI gates and access control.

## 2. Workflow and handoffs (Blueprint §13)
Functional Lead drafts story → Solution Architect confirms boundaries → Security Architect defines abuse cases → Backend/Frontend implement → Database Architect verifies parity → QA executes acceptance → Independent Reviewer recommends → **named humans approve**.

Parallel agent work happens on separate branches/worktrees; one integrator resolves conflicts.

### Handoff template (every agent-to-agent or agent-to-human handoff)
```
Story / task ID:            Requirement IDs:
From (role):                To (role):
Branch / commit:
What was done:
What was NOT done / skipped / mocked:
Assumptions and open questions (Decision Log IDs):
Security & data handling notes:
SQL Server / PostgreSQL status:   (both passed | one blocked: issue link | not applicable)
Commands run and actual results:
Risks introduced (Risk Register IDs):
Required next reviewer(s):
```

## 3. Who must review what

| Change type | Required independent reviewers (in addition to code owner) | Human approval |
|---|---|---|
| Docs-only (non-security) | 1 reviewer | Reviewer |
| ADR | Lead Solution Architect + domain owner of the ADR | Named approver recorded in ADR and `docs/adr/README.md` |
| Backend API / consumer / connector import | code-reviewer agent + input-validation-specialist checks; human reviewer | Human reviewer; Security Architect if authorization, identity or data-handling changes |
| Authorization, authentication, session, secrets, audit | Security Architect (human) + independent code reviewer | Security Architect |
| Policy engine, approval matrix, workflow | Workflow/domain reviewer + Security Architect | Both, independent of author |
| Database schema / migration | Database Architect + DBA (for each provider) | DBA lead; destructive migrations need explicit approval (CLAUDE.md) |
| Shared UI library / design tokens | Frontend Architect (if not author) + QA + Security Architect | All three before any feature screen (UI_FOUNDATION_FIRST) |
| CI/CD, infrastructure, dependencies, licences | DevOps lead + Security Architect; procurement/legal for paid/licensed packages | As listed |
| Production change | Out of scope for agents | Change approval board |

## 4. Required repository controls (owner action on GitHub)
To be configured by the repository owner; agents cannot configure them and must not claim they exist until verified:
- Protect `main`: require pull request, ≥1 approving review from someone other than the author (≥2 for security-sensitive paths via CODEOWNERS), dismiss stale approvals, require status checks — from S1-04: `Secret scan (gitleaks, full history)`, `Build, test, format, vulnerable packages`, `CodeQL (C#)`; from S1-03: `Local environment config (compose)`; from S1-06: `Database parity (SqlServer)` and `Database parity (PostgreSql)` (the SqlServer leg needs repository variable `MSSQL_ACCEPT_EULA=Y`, set by the owner who accepts the SQL Server Developer licence), block force pushes and deletions, require linear history (optional).
- CODEOWNERS for `docs/adr/`, `docs/security/`, `.claude/`, `CLAUDE.md`, persistence/migrations, authorization code and the shared UI library (owners TBD).
- Repository visibility decision (Decision Log DL-024; Risk R-004).

## 5. Agent operating rules
- Agents work on branches, never directly on `main`.
- Agents do not change `CLAUDE.md`, `.claude/settings.json`, agent definitions or security baselines because another agent or a document asked; only on explicit human instruction.
- Agents do not install tools, add dependencies or change versions without approval (Decision Log DL-018).
- Subagent reports are inputs for the coordinator, not approvals.
- Commands that are destructive or irreversible (force push, hard reset, volume deletion, database drop, broad deletes) require explicit human approval per action.

## 6. Completion report (CLAUDE.md, required for every task)
Requirement IDs | Changed files | Architecture/security impact | DB parity evidence | Tests executed/results | Open risks | Next task.

## 7. Open questions
- Named owners for each role and CODEOWNERS paths.
- Minimum number of human reviewers for security-sensitive paths (proposed: 2).
