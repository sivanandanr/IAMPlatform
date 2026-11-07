# Risk Register — Phase 0

Status: Working register | Created: 2026-11-07 (S1-01) | Owner: Lead Development Coordinator (named owner TBD)

Likelihood / Impact: L = Low, M = Medium, H = High. Ratings are initial assessments by the planning reviews, to be validated by the named risk owners. "Mitigation" lists planned actions, **not** implemented controls.

| ID | Risk | L | I | Mitigation (planned) | Linked | Owner (role) | Status |
|---|---|---|---|---|---|---|---|
| R-001 | Tenancy decided late → schema, key, cache and queue rework | M | H | Decide DL-001 before S1-06; ADR-0014 | DL-001 | Product owner | Open |
| R-002 | UI foundation not in blueprint plan → Phase 0 slips beyond 4 weeks, or P0-018 demo collides with UI gate | H | M | Decide DL-028; plan UI-0 in S1 and UI-1 in S2; API-level demo fallback | DL-028, ADR-0019 | Product owner | Open |
| R-003 | MassTransit licence/support window blocks or taxes P0-010 | M | M | Decide ADR-0021 early in Sprint 1; keep modules on Messaging abstractions | DL-018 | Lead Solution Architect | Open |
| R-004 | Repository is **public**; requirements, threat models or security findings could be exposed | M | H | DL-024: make repo private or keep sensitive docs out; review every push; no secrets/PII ever | DL-024 | Repository owner | Open |
| R-005 | No branch protection or independent review on `main` | H | H | Enable PR-required, independent review, no force push (owner action on GitHub); SEC-S1-15 | SEC-S1-15 | Repository owner | Open |
| R-006 | No real secret scanner yet (gitleaks not installed; `scripts/check-repo-security.py` is advisory and misses connection strings, JWTs, cloud keys, history) | H | H | Approve gitleaks; run locally and as CI gate over full history (S1-04) | DL-018 | DevOps lead | Open |
| R-007 | `.claude/settings.json` deny rules are Bash-only and bypassable (PowerShell, `rm -fr`, `git push -f`, `Get-Content .env`, `*.jks`) — false sense of control | H | M | Treat as best-effort only; enforcement via CI, branch protection and platform controls; review rules (no change without approval) | Security review F3 | Security Architect | Open |
| R-008 | Unverified executable (`docs/requirements/Claude Setup.exe`) in working tree | M | H | Ignored by `.gitignore`; owner to delete or move; verify signature out-of-band; never run from repo | DL-024 | Repository owner | Open |
| R-009 | Container parity ≠ Azure SQL MI behaviour (JSON type, ledger, RCSI, collation) | M | M | Pin versions (DL-019); staging smoke tests on real MI | ADR-0010 | DBA lead | Open |
| R-010 | Spec conflicts (precedence, workflow selection order, timeout actions, EESubgroupCode typing) implemented by assumption | M | H | Decision log entries; fail-closed configurable placeholders; no invented values | DL-006, 011, 013, 015 | Functional Lead | Open |
| R-011 | Policy AST bounds undecided → evaluation DoS or arbitrary limits | M | M | Proposed bounds (DL-008) behind configuration; sign-off before P0-008 | DL-008 | Security Architect | Open |
| R-012 | Audit hash-chain sealing (spec §12.6) not in Sprint 1 → could be mis-reported as done | M | H | ADR-0018 marks "NOT implemented"; completion reports must state it | ADR-0018 | Security Architect | Open |
| R-013 | Break-glass absent in Phase 0 skeleton → pressure to add hidden admin bypass | L | H | ADR-0008 forbids any bypass; dedicated ADR before implementation | DEC-004 | Security Architect | Open |
| R-014 | HR-derived PII in attribute values leaks via logs, audit or errors | M | H | Sensitivity-driven masking; redaction tests (SEC-S1-13); no sensitive data logging | DL-030, ADR-0015 | Security Architect | Open |
| R-015 | Commercial licence exposure from transitive or chosen packages (MediatR, AutoMapper, FluentAssertions v8, Duende, Redis, k6, AG Grid Enterprise, Docker Desktop) | M | M | DL-018 approvals; SCA licence scan in CI | DL-018 | Procurement / Legal | Open |
| R-016 | Schema-per-module × two engines increases migration/DBA overhead | M | L | Scripted identities and migrations; DBA review of ADR-0017 | ADR-0017 | DBA lead | Open |
| R-017 | Blueprint is "Proposed" → ADR-0001..0010 cannot be marked Accepted; downstream work proceeds on unapproved baseline | H | M | Obtain technical and security sign-off of blueprint early in Sprint 1 | Blueprint document control | Lead Solution Architect | Open |
| R-018 | Mock connectors or skipped tests represented as production-ready | L | H | CLAUDE.md rule 10; labels in code and reports; release gate | FR-REL-103 | QA lead | Open |
| R-019 | Slow builds: the 72-project skeleton (14 modules × 5 layers) took 7–15 min for a Release build on the dev machine (7m00s, then 14m48s; `dotnet format` 13m) with almost no code, which will slow feedback and CI | H | M | Investigate cause (antivirus scanning of `bin/obj`, analyzer cost, node reuse); measure on CI agent; if still slow, consider fewer assemblies per module (e.g. Contracts / Domain / Module) via an ADR-0001 amendment | ADR-0001, S1-02 | Lead Solution Architect | Open |

## Change history
| Date | Change | By |
|---|---|---|
| 2026-11-07 | Initial register from Sprint 1 planning reviews | Claude Code (S1-01), pending human review |
