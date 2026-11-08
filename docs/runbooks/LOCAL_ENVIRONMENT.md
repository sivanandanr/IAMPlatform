# Runbook — Local development environment (S1-03, P0-014)

Scope: **local development only.** Nothing here is a deployment artefact; never reuse these credentials, images or settings in a shared, test or production environment. Decision record: ADR-0023.

## Prerequisites
- Docker Engine or Docker Desktop with Compose v2 (Docker Desktop needs a paid subscription in larger organisations — DL-018).
- Bash (Git Bash on Windows) or PowerShell.
- ~4 GB free RAM for everything including SQL Server. The SQL Server image is amd64; it runs emulated (slowly) on ARM hosts.

## 1. Create your `.env` (once per machine)
```bash
scripts/dev-env-init.sh
```
or on Windows PowerShell:
```powershell
.\scripts\dev-env-init.ps1
```
This copies `infra/docker/dev.env.template` to `.env` at the repository root and replaces every `__GENERATE__` with a random secret. `.env` is gitignored — never commit, paste or share it. Re-running keeps an existing `.env`; `--force` / `-Force` regenerates it, but existing database volumes keep the **old** passwords (see "Reset" below).

SQL Server only: if **you** accept the Microsoft SQL Server Developer edition licence (non-production use), set `MSSQL_ACCEPT_EULA=Y` in your `.env`. Nobody accepts it on your behalf; without it the container refuses to start.

## 2. Start services
| Command | Starts |
|---|---|
| `docker compose up -d --wait` | Keycloak, Valkey, RabbitMQ, OTel Collector, Aspire dashboard |
| `docker compose --profile postgres up -d --wait` | the above + PostgreSQL 17 |
| `docker compose --profile sqlserver up -d --wait` | the above + SQL Server 2022 Developer |

Work against **one** database engine at a time unless you are running parity tests (ADR-0002).

## 3. Endpoints (all bound to 127.0.0.1)
| Service | Address | Credentials (`.env` keys) |
|---|---|---|
| PostgreSQL | `127.0.0.1:5432`, database `iam` | user `iam_owner` / `POSTGRES_PASSWORD` |
| SQL Server | `127.0.0.1:1433` (use `TrustServerCertificate=true` locally only) | `sa` / `MSSQL_SA_PASSWORD` |
| Keycloak | `http://localhost:8180` — issuer `http://localhost:8180/realms/iam-dev` | admin console: `KEYCLOAK_ADMIN_USER` / `KEYCLOAK_ADMIN_PASSWORD` |
| OIDC client | `iam-bff` (confidential, PKCE S256) | `IAM_BFF_CLIENT_SECRET`; redirect `${IAM_BFF_BASE_URL}/signin-oidc` |
| Test users | `dev.requester`, `dev.approver`, `dev.admin` | `IAM_DEV_USER_PASSWORD` |
| Valkey | `127.0.0.1:6379` | `VALKEY_PASSWORD` |
| RabbitMQ | AMQP `127.0.0.1:5672`, vhost `iam`; UI `http://localhost:15672` | `RABBITMQ_USER` / `RABBITMQ_PASSWORD` |
| OTLP | gRPC `127.0.0.1:4317`, HTTP `127.0.0.1:4318` | none (localhost only) |
| Aspire dashboard | `http://localhost:18888` | `ASPIRE_BROWSER_TOKEN` |

SA and `iam_owner` are bootstrap superusers. Application code must use least-privilege identities (created with S1-06), never these.

Test users have no roles or entitlements in the IdP; authorization is decided by the IAM platform. The realm is re-imported only when Keycloak starts with an empty store (the dev store lives in the container — recreate the container to pick up realm changes).

## 3a. Run the API and BFF against local Keycloak (S1-05)
One-time per machine:
```bash
dotnet dev-certs https --trust
```
```bash
dotnet user-secrets set "Oidc:ClientSecret" "<IAM_BFF_CLIENT_SECRET from your .env>" --project backend/src/Host/IAM.Bff
```
Then, in two terminals:
```bash
dotnet run --project backend/src/Host/IAM.Api --launch-profile "IAM.Api (local)"
```
```bash
dotnet run --project backend/src/Host/IAM.Bff --launch-profile "IAM.Bff (local)"
```
| Host | Address | Notes |
|---|---|---|
| IAM.Bff | `https://localhost:5001` | Browser entry point. `GET /bff/login?returnUrl=/`, `GET /bff/user`, `POST /bff/logout` (antiforgery), `/api/*` proxied |
| IAM.Api | `http://127.0.0.1:5100` | Bearer tokens only; browsers never call it directly |

Sign in as `dev.requester` (password `IAM_DEV_USER_PASSWORD`). After login, `https://localhost:5001/api/identity/me` returns your subject id via the proxy. Sessions are in memory: restarting the BFF signs everyone out (R-022). Realm changes (for example the `iam-api` audience mapper) need `docker compose up -d --force-recreate keycloak`.

## 4. Stop / reset
- Stop, keep data: `docker compose --profile postgres --profile sqlserver stop`
- Remove containers, keep data volumes: `docker compose --profile postgres --profile sqlserver down`
- **Delete data volumes** (`down -v`): destroys local databases and queues. Requires explicit human approval per CLAUDE.md; agents must not run it on their own. Needed after regenerating `.env` because database volumes keep the old passwords.

## 5. Troubleshooting
- `required variable ... is missing a value: run scripts/dev-env-init first` → create `.env` (step 1).
- SQL Server exits immediately → `MSSQL_ACCEPT_EULA` not set to `Y`, or less than 2 GB memory available to Docker.
- Port already in use → another local service holds the port; stop it (ports are fixed for now).
- Telemetry missing → `docker compose logs otel-collector`; an API-key mismatch shows as 401 from the dashboard.

## 6. Updating an image
Resolve the new digest (`docker buildx imagetools inspect <image>:<tag>`), update tag **and** digest together in `compose.yaml` and the table in ADR-0023, run the checks in ADR-0023 "Tests / evidence", and get DevOps/Security review.
