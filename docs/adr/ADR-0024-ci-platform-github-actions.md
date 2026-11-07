# ADR-0024: CI platform — GitHub Actions for PR checks
Status: Accepted by repository owner (sivanandanr, 2026-11-08, via Claude Code session) — independent DevOps/Security review pending | Date: 2026-11-08 | Decision owners: DevOps lead, Security Architect

Source: Blueprint v1.0 §3 ("Azure DevOps Pipelines"), §5 (`.azuredevops/`), §11 (CI/CD quality gates); SEC-S1-15; Decision Log DL-036, DL-018. Drafted by Claude Code (S1-04).

## Context and requirements
The blueprint assumes Azure DevOps Pipelines, but the repository is hosted on GitHub (github.com/sivanandanr/IAMPlatform). PR-stage gates (Blueprint §11): formatting, compile, analyzers, unit and architecture tests, secret scanning, SAST, SCA.

## Options considered
1. GitHub Actions — runs where the repository and pull requests are; checks can be required by branch protection.
2. Azure DevOps Pipelines connected to the GitHub repository (matches the blueprint).

## Decision and rationale
**Option 1 (owner decision, 2026-11-08).** Deviation from Blueprint §3/§5 recorded here. Deployment/promotion pipelines (Blueprint §11 "Deploy") are not decided by this ADR.

Workflows:
- `.github/workflows/pr.yml` — on PR to `main`, push to `main`, manual:
  - `secret-scan`: gitleaks CLI v8.30.1 (MIT), downloaded over HTTPS and verified by pinned SHA-256, scans the **full git history** with `--redact`.
  - `build-test`: SDK from `global.json`; locked restore; Release build (warnings as errors); `dotnet test` (Microsoft Testing Platform); `dotnet format --verify-no-changes`; vulnerable-package gate (`dotnet list package --vulnerable --include-transitive`).
- `.github/workflows/codeql.yml` — CodeQL C# `security-extended`, on PR, push to `main` and weekly.

Supply-chain controls: every action pinned to a full commit SHA (tag in a comment); workflow token `contents: read` by default, `security-events: write` only for CodeQL; `persist-credentials: false` on checkout; no secrets used by PR workflows.

The database parity matrix (ADR-0010) is added with S1-06, when persistence exists.

## Security and privacy impact
- PRs from forks run with a read-only token and no secrets (GitHub default for `pull_request`).
- gitleaks findings are redacted in logs.
- CodeQL results are visible in the repository's Security tab; on a public repository they are visible to repository admins/maintainers, not the public.

## SQL Server vs PostgreSQL impact
Parity job to be added (S1-06).

## Hybrid deployment impact
None for PR checks. Hosted runners have no access to private networks; integration with on-prem targets is out of scope for CI.

## Operational consequences and rollback
- Pinned SHAs need periodic updates (manual or Dependabot — not yet configured).
- Builds were 7–15 min on the developer machine (R-019); Linux runner time to be measured.
- Rollback: delete/disable workflows; Azure DevOps remains possible later.

## Tests / evidence
Local, 2026-11-08: vulnerable-package gate reports no vulnerable packages; all `uses:` pinned to 40-char SHAs; `.gitattributes` causes no line-ending renormalisation. Workflow YAML not yet linted (actionlint not installed) and **not yet executed on GitHub** — first evidence comes from the first PR run.

## Open questions and approvals
- Repository owner must make these checks **required** in branch protection for `main` (REVIEW_POLICY §4).
- Dependabot for action and NuGet updates (not yet approved).
- Deployment pipeline platform.
