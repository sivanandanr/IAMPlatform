# IAM Platform — backend

Status: **S1-06.** Solution skeleton (S1-02), BFF/OIDC sign-in and bearer-secured API (S1-05), and the EF Core persistence foundation on SQL Server and PostgreSQL (S1-06). No business features yet. Nothing here is production-ready.

## Layout (ADR-0001, ADR-0011)
```
backend/
  IAM.slnx
  src/BuildingBlocks/IAM.BuildingBlocks.Abstractions   (no dependencies)
  src/BuildingBlocks/IAM.BuildingBlocks.Persistence    (EF Core conventions, no database driver)
  src/BuildingBlocks/IAM.BuildingBlocks.Security       (host security helpers)
  src/Persistence/IAM.Persistence.{SqlServer,PostgreSql}  (provider setup + each provider's reviewed migrations)
  src/Host/IAM.Api                                     (composition root; references module Endpoints only)
  src/Host/IAM.Bff                                     (browser-facing BFF, ADR-0012/0013)
  src/Modules/<Module>/IAM.Modules.<Module>.{Contracts,Domain,Application,Infrastructure,Endpoints}
  tests/IAM.{Architecture,Api,Bff,Persistence}.Tests
```
Modules (14, ADR-0011): Identity, Organization, Catalog, Account, Policy, Workflow, Request, Provisioning, Governance, Audit, Integration, Notification, Administration, Lifecycle.

Other BuildingBlocks (Security, Persistence, Messaging, Observability), persistence provider projects, workers and connectors are added by the stories that need them (S1-05, S1-06, P0-010, P0-011, P0-012).

## Dependency rules (enforced by `tests/IAM.Architecture.Tests`)
| Layer | May reference (same module) | Other modules | BuildingBlocks | Packages / frameworks |
|---|---|---|---|---|
| Contracts | — | none | Abstractions | none |
| Domain | — | none | Abstractions | none |
| Application | Domain, Contracts | Contracts only | Abstractions, Messaging | approved packages |
| Infrastructure | Application, Domain, Contracts | Contracts only | Abstractions, Persistence, Messaging, Observability, Security | approved packages |
| Endpoints | Application, Contracts, Infrastructure | Contracts only | Abstractions, Security, Observability | ASP.NET Core |

Also enforced:
- The host references module Endpoints projects only.
- Workers and connectors never reference Endpoints or hosts.
- Database drivers (SqlClient, Npgsql, EF SqlServer) are allowed only in persistence projects.
- SQLite/InMemory providers, MediatR, AutoMapper, FluentAssertions and Duende are forbidden.
- Every project must be listed in `IAM.slnx`.

Rules are checked against the declared `.csproj` references **and** the compiled assembly references.

## Commands
Prerequisite: .NET SDK 10.0.403 or a later 10.0 patch (pinned in `/global.json`).
```bash
dotnet restore IAM.slnx
```
```bash
dotnet build IAM.slnx -c Release --no-restore
```
```bash
dotnet test --solution IAM.slnx -c Release --no-build --filter-not-trait "Category=DatabaseParity"
```
Database parity tests (Docker required; one engine per run, `SqlServer` also needs `MSSQL_ACCEPT_EULA=Y` from whoever accepts the licence):
```bash
IAM_PARITY_PROVIDER=PostgreSql dotnet test --project tests/IAM.Persistence.Tests -c Release --no-build --filter-trait "Category=DatabaseParity"
```
New migration (one per provider, both reviewed; see `docs/runbooks/LOCAL_ENVIRONMENT.md` §3b):
```bash
dotnet ef migrations add <Name> --context <Module>DbContext --project src/Persistence/IAM.Persistence.SqlServer --startup-project src/Persistence/IAM.Persistence.SqlServer --output-dir Migrations/<Module> --namespace IAM.Persistence.SqlServer.Migrations.<Module>
```
```bash
dotnet format IAM.slnx --verify-no-changes
```
Tests run on Microsoft.Testing.Platform (`/global.json` → `test.runner`).
Package versions are pinned centrally in `/Directory.Packages.props` with lock files (`packages.lock.json`). CI restores in locked mode (`ContinuousIntegrationBuild=true`).
