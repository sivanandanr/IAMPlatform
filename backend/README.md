# IAM Platform — backend

Status: **S1-02 skeleton.** The solution builds and the architecture tests run, but there are no endpoints, authentication, persistence or health checks yet. Nothing here is production-ready.

## Layout (ADR-0001, ADR-0011)
```
backend/
  IAM.slnx
  src/BuildingBlocks/IAM.BuildingBlocks.Abstractions   (no dependencies)
  src/Host/IAM.Api                                     (composition root; references module Endpoints only)
  src/Modules/<Module>/IAM.Modules.<Module>.{Contracts,Domain,Application,Infrastructure,Endpoints}
  tests/IAM.Architecture.Tests
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
dotnet test --solution IAM.slnx -c Release --no-build
```
```bash
dotnet format IAM.slnx --verify-no-changes
```
Tests run on Microsoft.Testing.Platform (`/global.json` → `test.runner`).
Package versions are pinned centrally in `/Directory.Packages.props` with lock files (`packages.lock.json`). CI restores in locked mode (`ContinuousIntegrationBuild=true`).
