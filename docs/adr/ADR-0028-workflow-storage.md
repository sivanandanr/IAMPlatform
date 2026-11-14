# ADR-0028: Workflow storage as a replayed action log
Status: Proposed (decisions taken by the owner as DL-072, 2026-11-14) — stage 5 slice 1 implemented; human sign-off pending | Date: 2026-11-14 | Decision owners: IAM Domain / Workflow lead, Database Architect, Security Architect (named approvers TBD)

Source: ADR-0005 (durable approval state machine, "persisted in the Workflow module's tables with optimistic concurrency"), ADR-0017 (persistence conventions), ADR-0018 (audit atomicity), IAM-WF-001, -004, -006, -009; DL-048, DL-068 to DL-072; R-032, R-036; CLAUDE.md rules 3, 9 and 10. Drafted by Claude Code.

## Context and requirements
The approval engine, approver resolution and the escalation permission check were pure code: nothing stored an instance, called the resolver when a level opened, or audited a decision (R-032, R-036). The engine is deterministic: the same actions at the same times always give the same history.

## Options considered
1. An append-only log of accepted actions, replayed on load and checked against the stored history (proposal).
2. Full state tables (stages, lists, decisions, escalations, proposals) restored by a checked `Restore` method.
3. One JSON snapshot of the instance per version.

## Decision and rationale
Option 1 (DL-072a).
- **Tables** (schema `workflow`, login `iam_app_workflow`): `ApprovalInstance`, a summary row (state, engine version, action count, whether provisioning is authorised, requester, beneficiary, the next deadline a timeout can act on), which also serialises writes through its row version; `ApprovalAction`, every accepted action and its inputs as JSON, numbered 1, 2, 3; `ApprovalHistory`, the engine's events, for queries and evidence.
- **One code path**: the live call and the replay both run each action through `ApprovalActionLog.Apply` from the same JSON. Loading replays the whole log and refuses the instance as damaged (audited, `workflow.approval.damaged`) unless the replay reproduces every stored history event, every action's version and the summary row. Tampering, a forged action, or an engine change that alters old outcomes fails closed.
- **Approvers at level open** (DL-069b): after every action, each level waiting for approvers is resolved through `IApproverResolver` and supplied, as its own logged action, so a replay never calls the resolver and always gets the people resolved at the time.
- **Starting** (DL-072b): `IApprovalWorkflow.StartAsync(binding, route)` is an internal interface (Workflow.Application) for the module that will own requests. There is no endpoint that starts an instance, so nobody can choose a route for their own request. A cross-module contract with its own DTOs comes with the Request module.
- **Acting**: decide, complete, return, withdraw, cancel, escalate, propose, approve and withdraw escalation, and apply timeouts. The actor is the signed-in user (`IAuditActorAccessor`), never a parameter. Escalation needs `workflow.approvals.escalate` (DL-070c); cancelling needs a new `workflow.instances.cancel`, held by IAM Manager by default (DL-072e). Each call is one transaction: load and check, act, supply opened levels, save, audit each action with its inputs. A refusal changes nothing and is audited (`workflow.approval.refused`) with its code.
- **Time** is the server's clock, truncated to microseconds before use so that what is stored replays exactly on both engines.

## Security and privacy impact
- Database guards on both engines: action and history rows are append-only and numbered without gaps (action versions never go back, the first action is the binding); the summary row's id, request item and people are immutable, it only moves forward, and a closed instance never changes state; nothing is deleted; PostgreSQL refuses TRUNCATE.
- The audit trail holds each action's inputs (binding, route, decisions, resolutions with their evidence). These contain sign-in ids and reasons, like the rest of the audit trail, and no attribute values.
- Remaining risks (R-037): the app login can still append a forged action and history together that replay consistently (the same class as R-033 to R-035; the planned fix is a keyed integrity hash, P0-013b); an engine change that alters old outcomes makes old instances refuse to load until a migration path exists; nothing sends authorisation to provisioning yet (no outbox, P0-010).

## SQL Server vs PostgreSQL impact
Same tables; AFTER triggers with `READCOMMITTEDLOCK` on SQL Server, BEFORE triggers with `FOR SHARE`, `ENABLE ALWAYS` and truncate guards on PostgreSQL. `Payload` is `nvarchar(max)` / `text`, bounded at 4,000,000 characters by the model. Same tests on both (`WorkflowParityTests`).

## Hybrid deployment impact
None.

## Operational consequences and rollback
New `workflow` schema and runtime login; the API needs `ConnectionStrings:workflow`, and the login needs the audit writer role. A damaged instance stays refused until people investigate; there is no repair tool yet. Down migrations drop the tables and their history; roll forward instead once data matters (DL-020).

## Tests / evidence
`IAM.Workflow.Tests/ApprovalActionLogTests` (every action kind survives its JSON and replays to the same instance), `Parity/WorkflowParityTests` (end to end with real resolution, refusals audited, permissions, two-person escalation, timeouts at server time, tamper detection, database guards). Results in the PR.

## Open questions and approvals
- Slice 2: the API, with the reading rule for participants and `workflow.instances.read`.
- Slice 3: the timeout worker.
- Engine versioning for replay, a repair path for damaged instances, the outbox event for provisioning, and the Request module.
- Independent security review and human sign-off.
