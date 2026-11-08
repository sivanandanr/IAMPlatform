# ADR-0023: Local development OIDC provider, cache distribution and local stack
Status: Accepted by repository owner (sivanandanr, 2026-11-08, via Claude Code session) — independent Security Architect / DevOps review and procurement/legal licence confirmation pending | Date: 2026-11-07, decided 2026-11-08 | Decision owners: Security Architect, DevOps lead; licence approval by procurement/legal (named approvers TBD)

Source: Blueprint v1.0 §3 (Redis; OIDC with Entra ID or approved IdP; OpenTelemetry), §12 ("Start SQL Server and PostgreSQL in separate Docker Compose profiles; include RabbitMQ, Redis, local OIDC test provider, OTEL collector and observability stack"), P0-014; CLAUDE.md (Redis for cache). Decision log DL-004, DL-018, DL-019. Drafted by Claude Code (S1-01, updated S1-03).

## Context and requirements
Developers need a local OIDC provider to exercise the BFF flow without real tenant credentials; a cache for immutable published policies and non-sensitive references only (Blueprint §3); both database engines in separate profiles; a broker; and a telemetry sink.

## Options considered
- Local OIDC: **Keycloak container (Apache-2.0)** / OpenIddict-based dev server (Apache-2.0, self-maintained) / Duende IdentityServer (commercial).
- Cache: Redis 8 (RSALv2 / SSPLv1 / AGPLv3) / **Valkey (BSD-3, protocol-compatible)**.
- Database versions (local): **PostgreSQL 17 + SQL Server 2022** / PostgreSQL 18 + SQL Server 2025.
- Telemetry: **OTel Collector + .NET Aspire dashboard (Apache-2.0 / MIT)** / Collector + Jaeger / Grafana LGTM (AGPLv3 components).

## Decision and rationale (owner, 2026-11-08)
| Component | Choice | Image (tag + digest resolved 2026-11-08) | Licence |
|---|---|---|---|
| Local OIDC | Keycloak, `start-dev`, realm `iam-dev` imported from `infra/docker/keycloak/import/` | `quay.io/keycloak/keycloak:26.8.2` | Apache-2.0 |
| Cache | Valkey — **deviation from Blueprint wording "Redis"** (licence) | `valkey/valkey:8.1.11-alpine3.24` | BSD-3 |
| PostgreSQL (profile `postgres`) | 17 | `postgres:17.11-alpine3.24` | PostgreSQL |
| SQL Server (profile `sqlserver`) | 2022 Developer edition, CU27 GDR1 | `mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04` | Microsoft EULA, **non-production** |
| Broker | RabbitMQ 4.3 with management plugin | `rabbitmq:4.3.7-management-alpine` | MPL-2.0 |
| Telemetry | OTel Collector (contrib) → Aspire dashboard | `otel/opentelemetry-collector-contrib:0.165.0`, `mcr.microsoft.com/dotnet/aspire-dashboard:13.6.4` | Apache-2.0, MIT |

The full digests are in `compose.yaml`. Versions are local-development choices only; the **supported production version matrix remains open (DL-019)**. The conservative choice (oldest widely deployed on-prem major versions) keeps newer engine features from slipping into shared code.

## Security controls (local stack)
- Every published port binds to `127.0.0.1`; verified unreachable from the LAN address.
- No secret is committed. `scripts/dev-env-init.{sh,ps1}` generate `.env` (gitignored) from `infra/docker/dev.env.template`; compose fails if any secret is missing. Keycloak `${ENV}` placeholders put the client secret and test-user password into the realm at import time.
- SQL Server EULA is never accepted on a developer's behalf: `MSSQL_ACCEPT_EULA` is empty by default and the container refuses to start until the developer sets it.
- Keycloak client `iam-bff`: confidential, authorization code + **PKCE S256 required**, implicit/password/service-account grants off, exact redirect URI, `fullScopeAllowed: false`, explicit default scopes and **no `offline_access`**, refresh-token rotation (`revokeRefreshToken`, max reuse 0), brute-force protection, 5-minute access tokens, self-registration off. Test users carry no roles: authorization stays in the IAM platform (ADR-0008).
- Valkey: password required (passed as stdin config, not in process arguments), no persistence (`save ''`, no AOF), bounded memory.
- RabbitMQ: no `guest` user; generated credentials; vhost `iam`.
- PostgreSQL: SCRAM-SHA-256 for host and local connections.
- Collector deletes all `http.request.header.*` / `http.response.header.*`, `url.query`, `url.full`, `http.url`, `db.statement`, `db.query.text` attributes and masks bearer/JWT strings in log bodies. **Partial control (CLAUDE.md rule 10):** resource attributes, span events and exception messages are not cleaned; application code must not emit secrets in the first place. Dashboard UI requires a browser token (not written to container logs); OTLP into the dashboard requires an API key and is not published to the host.
- `scripts/dev-env-init.sh` writes `.env` with mode 600; `dev-env-init.ps1` writes a temp file with an ACL for the current user only (inheritance removed) and renames it into place.
- All containers run with `no-new-privileges` and memory limits; the collector filesystem is read-only.

**Still required (S1-05), not implemented yet:**
- The IAM API must refuse to start if the dev issuer (`http://localhost:8180/realms/iam-dev`) is configured outside the Development environment (issuer allowlist); `RequireHttpsMetadata=false` allowed only in Development and covered by the same startup check.
- Keycloak does not enforce `nonce`; the BFF must generate and validate it (and `state`, PKCE) itself — S1-05 acceptance criterion.
- S1-06: least-privilege migration and application roles; never put `sa` / `iam_owner` connection strings in appsettings.

## Security and privacy impact
Test IdP issuer must never be trusted in non-dev environments. Cache never stores tokens, sessions with secrets, or sensitive attributes unless explicitly approved and isolated. Keycloak dev mode uses HTTP and an embedded H2 store — local only.

## SQL Server vs PostgreSQL impact
Both engines available as separate compose profiles. SA / `iam_owner` are bootstrap superusers only; least-privilege application identities are created with S1-06 (ADR-0017).

## Hybrid deployment impact
None (dev only).

## Operational consequences and rollback
Swapping Valkey ↔ Redis is protocol-compatible for typical use. Image updates: re-resolve digest, update tag and digest together. Runbook: `docs/runbooks/LOCAL_ENVIRONMENT.md`.

## Tests / evidence (2026-11-08, Windows 11, Docker 29.9.2, Compose v5.6.1)
- `docker compose --profile postgres --profile sqlserver config --quiet` → OK. Collector `validate` → exit 0.
- `docker compose --profile postgres up -d --wait` → keycloak, valkey, rabbitmq, postgres healthy; collector and dashboard running.
- Keycloak: discovery issuer `http://localhost:8180/realms/iam-dev`; auth request with S256 → 200; without PKCE → `Missing parameter: code_challenge_method`; with `plain` → rejected; foreign redirect URI → `Invalid parameter: redirect_uri`; password grant → `unauthorized_client`; client secret equals `.env` value (compared in-container, not printed).
- Valkey without password → `NOAUTH`; `save` empty; `allkeys-lru`. RabbitMQ users: `iam` only; vhost `iam`. PostgreSQL 17.11, `scram-sha-256`, wrong password rejected. Dashboard without token → `/login`.
- Collector: OTLP/HTTP ingest 200, no export errors; same processors with a debug exporter → credential attributes and query removed, `url.path` kept (initial attribute list; re-validated with `validate` after the review changes).
- After independent security review (advisory, Security Architect agent): realm re-imported — `scope=openid offline_access` → `invalid_scope`; `revokeRefreshToken=true`, `refreshTokenMaxReuse=0`; PKCE still enforced; client secret still matches. Valkey password absent from process arguments; unauthenticated `ping` → `NOAUTH`. Container logs of all services checked (inside a throwaway container) for every generated secret: none found after setting the dashboard log level to Warning (the browser token was logged before that fix).
- **Not verified:** SQL Server container start (needs a developer's own EULA acceptance); `dev-env-init.ps1` not executed, including its ACL hardening (blocked by session permission rules; only the `.sh` variant ran); log-body masking not exercised with sample data; test-user login end-to-end (password substitution uses the same mechanism as the verified client secret); Linux/macOS hosts; ARM hosts.

## Open questions and approvals
- Production IdP (Entra ID or other) and multi-IdP routing (spec §12.1) — DL-004.
- Licence confirmation by procurement/legal: Valkey, Keycloak, SQL Server Developer edition, Docker Desktop subscription — DL-018.
- Production database version matrix — DL-019.
- Independent Security Architect and DevOps review (REVIEW_POLICY §3).
