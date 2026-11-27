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

## 3b. Database provider, connection strings and parity tests (S1-06)
- `IAM.Api` needs `IAM_DB_PROVIDER` (`SqlServer` or `PostgreSql`, exact; no default) and one connection string per module schema, `ConnectionStrings:<schema>` (today `identity`, `configuration` for the Administration module's access control (P0-013), `catalog` for the application catalog (DL-066), `organization` for workgroups (DL-068), `workflow` for stored approvals (DL-072), `account` for accounts and naming rules (DL-089), `policy` for the approval matrix (DL-090), and `request` for request items (DL-091)). It refuses to start otherwise. To set the first administrator and manager locally, see `ACCESS_CONTROL_BOOTSTRAP.md`. The local launch profile sets `IAM_DB_PROVIDER=PostgreSql`; put the connection string in user secrets, never in appsettings:
```bash
dotnet user-secrets set "ConnectionStrings:identity" "<connection string for the iam_app_identity login>" --project backend/src/Host/IAM.Api
dotnet user-secrets set "ConnectionStrings:configuration" "<connection string for the iam_app_administration login>" --project backend/src/Host/IAM.Api
dotnet user-secrets set "ConnectionStrings:catalog" "<connection string for the iam_app_catalog login>" --project backend/src/Host/IAM.Api
dotnet user-secrets set "ConnectionStrings:organization" "<connection string for the iam_app_organization login>" --project backend/src/Host/IAM.Api
dotnet user-secrets set "ConnectionStrings:workflow" "<connection string for the iam_app_workflow login>" --project backend/src/Host/IAM.Api
dotnet user-secrets set "ConnectionStrings:account" "<connection string for the iam_app_account login>" --project backend/src/Host/IAM.Api
dotnet user-secrets set "ConnectionStrings:policy" "<connection string for the iam_app_policy login>" --project backend/src/Host/IAM.Api
dotnet user-secrets set "ConnectionStrings:request" "<connection string for the iam_app_request login>" --project backend/src/Host/IAM.Api
```
- Application logins are least-privilege per module and are **not** `sa` / `iam_owner`. Create them with the bootstrap script for your engine (run as administrator, inputs bound as parameters): `infra/db/sqlserver/bootstrap-module.sql` or `infra/db/postgresql/bootstrap-module.sql`. For the audit module (S1-07) run `bootstrap-module.sql` for schema `audit`, then `grant-audit-role.sql` with role `iam_audit_writer` once for every module runtime login (including the audit login) and with `iam_audit_reader` for the audit login, before the audit migration — the migration fails if either role is missing. PostgreSQL 16 or later is required. Migrations run as `iam_migrator`; the API never runs migrations. There are no tables to query yet (S1-08 adds the first ones), so a local database for the API is optional until then.
- Parity tests start their own throwaway containers from the same pinned images (Docker required) and never touch the compose databases:
```bash
cd backend && IAM_PARITY_PROVIDER=PostgreSql dotnet test --project tests/IAM.Persistence.Tests --filter-trait "Category=DatabaseParity"
```
  For SQL Server set `IAM_PARITY_PROVIDER=SqlServer` and, only if **you** accept the SQL Server Developer licence, `MSSQL_ACCEPT_EULA=Y`. The tests fail rather than accept it for you.
- New migrations: add one for **each** provider with `dotnet ef migrations add` (see `backend/README.md`), review both, and keep `IdentityMigrationsMatchTheModel`-style tests green. Recovery from a bad migration is restore/PITR plus a compensating forward migration (DL-020); there are no down migrations.

## 3c. Workflow worker (DL-074)
`IAM.Workers.Workflow` applies approval deadlines in the background. It needs `IAM_DB_PROVIDER` and the `workflow`, `catalog`, `organization` and (since DL-096, to read frozen request items) `request` connection strings (user secrets, as for the API), and optionally `Workflow:Timeouts:IntervalSeconds` (5 to 3600, default 60) and `Workflow:Timeouts:BatchSize` (1 to 500, default 50):
```bash
dotnet user-secrets set "ConnectionStrings:workflow" "<connection string for the iam_app_workflow login>" --project backend/src/Workers/IAM.Workers.Workflow
```
```bash
IAM_DB_PROVIDER=PostgreSql dotnet run --project backend/src/Workers/IAM.Workers.Workflow
```
Since DL-077 it also sends the workflow outbox to RabbitMQ, so it needs the broker too: `Messaging:RabbitMq:Host`, `VirtualHost`, `UserName` and `Password` (user secrets; locally the workflow module's own broker user from §3d, vhost `iam`, never `guest`), and `Messaging:RabbitMq:UseTls=false` only because the broker is on this machine. `Workflow:Outbox:*` tunes the relay (interval, batch, attempts, publish timeout, lease). Apply the broker topology and users from §3d first: the worker creates nothing on the broker. Messages nobody consumes wait in `iam.unrouted` for at most 7 days; a message whose send failed `MaxAttempts` times, or whose stored content breaks the envelope rules, is parked (logged at Error with its id and type) until the setting is raised.

Since DL-091 it also starts the approvals of submitted request items from the `iam.workflow.request_items` queue (`Workflow:Consumer:*` as for the notification consumer), so its broker user needs the read and retry permissions below. It writes as `system:workflow-timeouts`. A damaged instance is skipped until restart, and so is any instance that fails 3 passes in a row (logged at Error with the instance id only); look damaged ones up in the audit trail (`workflow.approval.damaged`, written once per instance) or `workflow.ApprovalDamage`. It is not in compose yet.

## 3d. Notification worker, broker topology and users (DL-078, DL-079)
`IAM.Workers.Notification` turns workflow `task_opened` events into pending notifications (nothing is sent yet). It needs `IAM_DB_PROVIDER`, `ConnectionStrings:notification` (the `iam_app_notification` login, bootstrapped like the other modules), the broker settings as for the workflow worker but with the notification module's own broker user, and optionally `Notification:Consumer:Prefetch` (1 to 100, default 10), `DeliveryLimit` (1 to 15, default 5) and `ReconnectDelaySeconds` (1 to 60, default 5).

No worker creates anything on the broker. As the broker's administrator, apply the reviewed topology (exchanges, queues with their limits, bindings), then each module's user with exactly the permissions in `infra/rabbitmq/permissions.json` (never `guest`):
```bash
docker compose cp infra/rabbitmq/definitions.json rabbitmq:/tmp/iam-definitions.json
```
```bash
docker compose exec rabbitmq rabbitmqctl import_definitions /tmp/iam-definitions.json
```
```bash
docker compose exec rabbitmq rabbitmqctl add_user iam-workflow '<password>'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_permissions -p iam iam-workflow '^$' '^iam\.(workflow|unrouted)$' '^$'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_topic_permissions -p iam iam-workflow iam.workflow '^workflow\.approval\.[a-z_]+$' '^$'
```
```bash
docker compose exec rabbitmq rabbitmqctl add_user iam-notification '<password>'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_permissions -p iam iam-notification '^$' '^iam\.notification\.approval_tasks\.retry$' '^iam\.notification\.approval_tasks$'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_topic_permissions -p iam iam-notification iam.workflow '^$' '^$'
```
The workflow user (DL-091) also consumes submitted request items, and the request module has its own user for its relay:
```bash
docker compose exec rabbitmq rabbitmqctl set_permissions -p iam iam-workflow '^$' '^iam\.(workflow|unrouted|workflow\.request_items\.retry)$' '^iam\.workflow\.request_items$'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_topic_permissions -p iam iam-workflow iam.request '^$' '^$'
```
```bash
docker compose exec rabbitmq rabbitmqctl add_user iam-request '<password>'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_permissions -p iam iam-request '^$' '^iam\.(request|unrouted)$' '^$'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_topic_permissions -p iam iam-request iam.request '^request\.item\.[a-z_]+$' '^$'
```
`IAM.Workers.Request` sends the request module's outbox: it needs `IAM_DB_PROVIDER`, `ConnectionStrings:request`, the broker settings with the `iam-request` user, and optionally `Request:Outbox:*` as for the workflow relay. It writes as `system:request-relay` and reads no other schema.

The provisioning module (DL-093) has its own user, which consumes approvals' `authorized` and `closed` events:
```bash
docker compose exec rabbitmq rabbitmqctl add_user iam-provisioning '<password>'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_permissions -p iam iam-provisioning '^$' '^iam\.provisioning\.approvals\.retry$' '^iam\.provisioning\.approvals$'
```
```bash
docker compose exec rabbitmq rabbitmqctl set_topic_permissions -p iam iam-provisioning iam.workflow '^$' '^$'
```
`IAM.Workers.Provisioning` checks each approval against the audit store and plans connector operations (it runs none yet). It needs `IAM_DB_PROVIDER`; `ConnectionStrings:provisioning` (the `iam_app_provisioning` login, bootstrapped like the other modules and granted `iam_audit_writer`); `ConnectionStrings:workflow`, `request`, `catalog` and `account` (those modules' own logins, read only here); `ConnectionStrings:audit` (the audit login, `iam_audit_reader` only); the broker settings with the `iam-provisioning` user; optionally `Provisioning:Consumer:*` as for the notification worker; and the reviewed allowlist `Provisioning:Connectors:N:ApplicationId`, `ConnectorKey` (only `mock-ad`, never production-ready), `AccountContainer` (an OU path such as `OU=Users,DC=example,DC=test`) and, since DL-098, `GroupContainer` (the only OU path the application's entitlement groups may be in; any other group is blocked as `EntitlementOutOfScope`). An application not in the list is blocked as manual work. It writes as `system:provisioning`. Blocked items (`State = 'Blocked'`, `Problem` the reason) are in `provisioning.ProvisioningItem` and audited as `provisioning.item.blocked`.

Since DL-094 the worker also runs planned items: `Provisioning:Runner:IntervalSeconds` (1 to 3,600, default 10), `BatchSize` (1 to 500, default 20), `LeaseSeconds` (30 to 3,600, default 300), `MaxAttempts` (1 to 50, default 5) and `RetryDelaySeconds` (1 to 3,600, default 30, doubling per attempt up to an hour). Since DL-097, `StepTimeoutSeconds` (1 to half the lease, default 60 or half the lease) limits each call to the target, and a run taken over more than `MaxAttempts` times fails as `RunnerError`. The only target is the mock AD connector (`Provisioning:Targets:mock-ad:BaseDn`, default `DC=example,DC=test`): in memory, empty at start, never production-ready, so its containers and groups have to exist before it can create anything. New accounts are recorded in `account.Account` (origin `Created`, verified) with the account login. Blocked and failed items are listed at `GET /api/provisioning/items?state=Blocked` (or `Failed`) and `GET /api/provisioning/items/{id}` for holders of `provisioning.items.read` (IAM Manager, Auditor); the API host now needs `ConnectionStrings:provisioning`. Every audit record of one request shares its correlation id: search the audit by it to follow a request from submission to the target.

Since DL-099 a failed item may show undo steps (11 and 12): `UndoFailed` means the target didn't confirm the undo and a person must remove the access by hand; `Interrupted` means a runner stopped during an undo and the next one finished it; `TargetUnreadable` retries are temporary and count as attempts; `NameNotApproved` blocks an item whose account name isn't the one its approval recorded. Account target ids must be printable ASCII; before applying the `AccountTargetCanonical` migration, recompute by hand the key of any stored DN with spaces around separators or escapes. Migrations are forward-only: their Down steps are for an empty development database (DL-020).

Since DL-100: `Provisioning:Runner:UndoMaxAttempts` (1 to 100, default 10) and `MaxPostponements` (1 to 1,000, default 20). An undo calls the target only for steps with an audited `step_done` record in the application's own scope; `UndoFailed` with nothing called means the stored steps couldn't be trusted, and a person must check the target by hand. `KeptForOtherItem` on an undo step means another item still holds that access. `RecordedAfterUndo` means an account record appeared while its access was being undone: reconcile the record and the target by hand. Items planned before DL-100 have no step fingerprints in their audit, so an undo of them fails closed (`UndoFailed`). Target ids must be a DN of named attributes or a plain GUID; before deploying, list stored target ids that don't pass `AccountRules.ValidateTargetId` and fix them by hand.

Since DL-101: `OutcomeUnknown` means a create or entitlement add broke off and nobody can tell what it changed: on an existing account nothing was removed, so check the target and the person's access by hand. An undo that waits for another item on the same access shows `UndoRetry` until that item finishes. Completing an Action level of an item with a new account's name now requires `expectedAccountName` (refused `AccountNameNotConfirmed` otherwise). Release notes for the upgrade: (1) migration `AccountNameOrder` stops if any item's account names are numbered out of the order they were written; resolve those items by hand, then run it again; (2) target-id DNs may use only CN, OU, DC, O, C, L, ST and UID, and runs of spaces now key as one space: before deploying, list stored target ids that fail `AccountRules.ValidateTargetId` or whose `TargetIdKey` changes, and fix them by hand; (3) grant locks held during the upgrade only run out; (4) items whose steps ran before DL-100 have no fingerprints, so an undo of them ends `UndoFailed` for a person.

Since DL-102: `OutcomeUnknown` now also ends an item whose create or add broke off on an earlier attempt and was never settled, and a create that, run again, found its own object may be there: check the target (the account may exist, enabled or not) and reconcile by hand. A step's `Unsettled` flag can't be cleared. A connector fault on a write (`ConnectorError`, `TimedOut`, `InvalidResponse`) is treated as unknown. An undo waiting on another item removes the access on its last pass. Upgrade notes: migration `AccountNameSeqGuard` stops if an item has two account names with the same `SetAtUtc`; resolve them by hand, then run it again. Stored target-id keys containing escaped runs of spaces must be recomputed by hand.

Since DL-103: every failed create or add except `Throttled` is treated as unknown, so a connector reporting `TargetUnavailable` after a write marks the step unsettled; expect more `OutcomeUnknown` items to reconcile. An existing account's item holds its grant lock from the add until its success or failure is saved, so a waiting undo may show `UndoRetry` a little longer. Upgrade note: migration `UnsettledBackfill` marks unsettled every planned create or add of an item already tried; such items may end `OutcomeUnknown` for a person rather than trust an "already there".

Since DL-104: only the SDK's speed limit (`RateLimited`) is retried as "nothing written"; a connector reporting `Throttled` is treated as unknown, like `TargetUnavailable`. An item that finds its grant held by another item is postponed (`GrantBusy` in the audit) without using an attempt, up to `MaxPostponements`; check for a stuck holder if it keeps recurring. A run whose grant lock was taken while it read the target stops without recording success (`LogRunStopped`); the item is decided again on its next run. Upgrade note: run migrations with the provisioning workers stopped, since the DL-103 backfill writes no audit event for the rows it marks.

Since DL-105: a connector not built on the SDK's `ConnectorBase` never gets a plain retry for a failed write; every failure from it is treated as unknown. A connector reporting `RateLimited` on a read is retried as `ConnectorError`. The worker refuses a ledger row stored as `Failed` (the SDK never writes one): treat it as tampering, find who wrote it, and reconcile the item by hand. A run that stops with `LogRunStopped` right after its final read lost its grant lock; the item is decided again after its lease runs out.

Since DL-106: a connector built on `ConnectorBase` that implements `IConnector.ExecuteAsync` itself is treated like one not on the SDK (every failed write unknown); new connectors must leave `ExecuteAsync` to the SDK. A step audited with the code `LedgerDamaged` (and an error log naming the item) means the connector's ledger holds a record the SDK never writes: treat it as tampering, find who wrote it, and reconcile the item by hand. Upgrade note: migration `LedgerNoFailed` stops the upgrade if the operation ledger holds a `Failed` row (error 51002 on SQL Server); nothing is deleted, so investigate and resolve the row before upgrading again.

Change topology only by changing `definitions.json` through review and importing it again; a queue's arguments can't change in place (the worker logs `BrokerPreconditionFailed` until the queue is recreated). Unprocessable messages are in `iam.notification.approval_tasks.dlq` (logged with id and type once validated); there is no replay tool yet. `iam.unrouted` keeps events no consumer takes for at most 7 days (100,000 messages, 100 MB, oldest dropped): it is not a source to replay from.

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
