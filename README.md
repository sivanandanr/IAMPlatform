# Secure Claude Code starter for IAM Platform (Phase 0)

Designed for the IAM Functional Specification v3.2 and Phase 0 blueprint: ASP.NET Core 10, Angular, dual SQL Server/PostgreSQL provider, hybrid connectivity, modular monolith and independently deployed workers.

## Install
1. Extract into the **root** of your IAM repository (merge `.claude/`; do not overwrite existing instructions without reviewing differences).
2. Place the v3.2 functional specification and Phase 0 blueprint under `docs/requirements/` using the filenames documented in `CLAUDE.md`. These source documents are **not bundled**.
3. Review `.claude/settings.json` and tailor tool permissions to your Claude Code version and company policy. Keep `.claude/settings.local.json` untracked.
4. Run `claude` in the repository root. Ask Claude to use `functional-lead` and `solution-architect` to plan Phase 0 Sprint 1. Do not grant blanket unattended execution permissions.
5. For reusable prompts, use `/phase0-plan`, `/security-review`, `/threat-model`, `/db-parity`, `/release-gate` (command availability depends on Claude Code version).

## Local development environment
Docker Compose dependencies (Keycloak, Valkey, RabbitMQ, OTel Collector + Aspire dashboard, PostgreSQL 17 or SQL Server 2022 profiles): see [docs/runbooks/LOCAL_ENVIRONMENT.md](docs/runbooks/LOCAL_ENVIRONMENT.md). Local only; secrets are generated into a gitignored `.env`.

## Frontend
Angular 22 workspace with the first-party UI library `@iam/ui` and the AG Grid wrapper `@iam/ui/grid` (S1-09, ADR-0019): see [frontend/README.md](frontend/README.md).

## Optional features (Proposed, not built yet)
AR-1 is off until a deployment turns it on. AR-2 and AR-3 do nothing until they are configured. See [docs/requirements/requirements.md](docs/requirements/requirements.md).

## Security boundaries
- Agents are advisory and code-generating assistants, **not** a substitute for peer review, penetration testing, IAM security sign-off or production change approval.
- Never send real credentials, tokens, personal data, customer identity exports, production logs or restricted diagrams to an AI assistant unless explicitly approved by your organization.
- Configuration does not by itself enforce enterprise policy: protect branches, CI/CD, secrets, production access and reviewer independence at the platform level.
- No automated hook is enabled by default; an incorrectly configured hook may create a false sense of security. Use CI checks as the enforcement layer.
- Read `docs/security/SECURITY_BASELINE.md` and `docs/security/THREAT_MODEL_TEMPLATE.md` before coding.

## Included
- `CLAUDE.md` main architecture/behavior rules
- 11 specialist agents in `.claude/agents/`
- 5 project slash commands in `.claude/commands/`
- `.claude/settings.json` conservative deny rules
- security standards, templates, quality gates, requirement traceability and ADR template
- `scripts/check-repo-security.py` basic repository hygiene scan (advisory; not a full secrets scanner)
- `.gitignore.fragment` to merge into your repo's `.gitignore`

## First prompt
"Read CLAUDE.md and docs/security/SECURITY_BASELINE.md. Use the functional-lead, solution-architect and security-architect agents to prepare Phase 0 Sprint 1 implementation plan only. Do not edit code until I approve the plan."

## v1.1 payload validation update
Added `docs/security/BACKEND_INPUT_VALIDATION.md`, `input-validation-specialist` agent and `/validate-payload-security` command; updated project rules, security baseline, quality gate and relevant agents.

## v1.2 custom Angular UI policy
Added `docs/frontend/COMPONENT_ARCHITECTURE.md`, `docs/frontend/COMPONENT_INVENTORY.md`, `frontend-architect` agent and `/design-ui-control` command. Common controls are in-house; Kendo UI and PrimeNG are references only; AG Grid is approved exclusively behind an IAM wrapper. Updated CLAUDE.md and review agents with frontend security/accessibility gates.

## v1.3 — UI foundation first
Added `docs/frontend/UI_FOUNDATION_FIRST.md`, `/build-ui-foundation` command, mandatory gate in `CLAUDE.md` and agent instructions. Run `/build-ui-foundation` before implementing IAM feature screens.
