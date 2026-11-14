# ADR-0005: Durable approval workflow state machine
Status: Proposed, proof of concept implemented (S2-02 and stage 1 of AR-1 to AR-3; not production-ready) | Date: 2026-11-07, updated 2026-11-10 | Decision owners: IAM Domain / Workflow lead, Security Architect (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 005), §9, §10; Spec v3.2 Module 7, IAM-WF-001, -003, -004, -006, -009, -011, -012, K.1, K.6; CLAUDE.md rule 3. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Multi-stage approvals (sequential / parallel, quorum ALL / ANY-ONE / N), escalation and replay-safe decisions. No self-approval; mandatory stages cannot be bypassed; no approver, ambiguity or timeout must never grant.

## Options considered
1. Application-owned state machine persisted in the Workflow module's tables with optimistic concurrency (proposal).
2. External workflow engine (e.g. Elsa, Temporal, Camunda).
3. Saga state machine inside the messaging library.

## Decision and rationale
Option 1 for Phase 0 (small, auditable, no new runtime dependency).
- **ApprovalInstance**: Created → Bound (policy version, matched rule, resolved approvers, snapshot) → InProgress → Approved | Rejected | Cancelled | Withdrawn | Returned. Held/Escalated reachable from Bound/InProgress; returns to InProgress only when a valid route resolves. Provisioning is authorized after the last decision level, before Approved if action levels remain (DL-055), and that is what the outbox story must emit: waiting for Approved would hold provisioning until the manual work it comes before was finished.
- **StageInstance**: Pending → Active → Approved | Rejected | Escalated | Cancelled | Completed | Returned. No stage is ever skipped (DL-057). NotApplicable only when a stage condition evaluated false at bind time (recorded); mandatory stages can never be NotApplicable via override (IAM-WF-010).
- **Decision**: Approve / Reject / Complete with actor, delegate-for, reason, timestamp and expected stage version; optimistic concurrency; idempotent (duplicate/replay rejected); quorum evaluated transactionally; ALL/N need distinct eligible identities; one Reject on a required stage rejects the item.
- Self-approval: requester, beneficiary or policy maker removed from candidates; if none remain → "no approver" → hold/escalate.
- Timeout (DL-013, decided 2026-11-09): escalate, reject or cancel only — never approve, never skip a stage.
- Reject (DL-042, decided 2026-11-09): any eligible Reject on a stage rejects the whole request, on ALL, ANY-ONE and minimum-N alike.
- Same approver across stages (DL-010, decided in part 2026-11-09): allowed unless a stage is marked `independent`.
- Escalation (DL-043, DL-047, decided 2026-11-09): only a held stage; nobody on an open or pending stage's list may escalate, and an escalator cannot be a decider on the same request, in either order. Escalation never closes a stage.
- ALL (DL-044, decided 2026-11-09): escalation cannot take the list below its size when the stage opened, and an independence conflict when a stage opens holds it.

## Security and privacy impact
Server-side approver eligibility and independence checks; decisions audited with policy version/hash (IAM-WF-009).

## SQL Server vs PostgreSQL impact
Concurrency via application `Version` column (ADR-0017) for identical semantics; deadlock/serialization retry codes differ (1205/3960 vs 40001/40P01) — retry only idempotent units.

## Hybrid deployment impact
None.

## Operational consequences and rollback
Instances are bound to immutable policy versions; reevaluation only on controlled triggers (IAM-WF-012).

## Tests / evidence
Sequential + parallel ALL + workgroup ANY-ONE; self-approval blocked with alternate resolved; concurrent decisions → exactly one outcome; timeout never approves; mandatory stage cannot be removed. Results for the proof of concept are below. Concurrent decisions are **not proven**: the class is not thread-safe, and version checks were only tested one call at a time.

## Proof of concept (S2-02, P0-009)
Pure code in `Workflow.Domain/Approvals`: `ApprovalRoute` and `ApprovalInstance`. No tables, endpoints, events or packages, so database parity does not apply yet. **`ProvisioningAuthorized` needs every applicable decision level approved, and nothing acts on it yet.**

Differences from the states above, for review:
- **No Created or Bound state:** `Bind` validates everything and starts the first step in one call. The binding is kept on the instance; the `Bound` history event holds only its kind and time. A stage has no Escalated state: it stays held until escalated to a list that can meet its quorum, then returns to Active with an escalation count.
- **Parallel stages** are stages with the same sequence number. The next step opens only when all of them are finished: approved or completed.
- **Decisions** are Approve or Reject, and action levels are finished with `CompleteAction` (stage 1 below). The expected version is per instance, not per stage as described above, so approvers on parallel stages can refuse each other with a stale version. This should be settled before the schema is designed (DL-046).

Details decided in the build, for review:
- **Independence works both ways:** someone who decided elsewhere cannot decide on an independent stage, and someone who decided on an independent stage cannot decide elsewhere. The rule applies in either order, though who decides first can still change who is left to decide. This includes delegates and the people they act for.
- **ALL** means everyone on the list when the stage opened (or was escalated), except the requester, beneficiary and policy maker, who are removed. That list is then fixed: anyone on it who has an independence conflict, or who has already acted for someone else on the stage, holds the stage rather than it quietly needing fewer people.
- **Held instances accept no decisions or Return anywhere**, including on parallel stages, until a valid route is given. Tasks are refused too, until authorization (below).
- **Reject on a parallel step** cancels its sibling stages and every later stage.
- **Delegation:** the delegator and the delegate must both be eligible, and one person counts once on a stage. Whether a delegation exists, and who the actor is, are checked by the calling layer, which does not exist yet.
- **Escalation** applies only to a held stage, reopens it, and never closes it: only a later decision can. An escalation that could not reopen the stage is refused and records nothing, so it cannot use up the cap. It needs a reason and records who changed the list, from whom to whom, and the deadline afterwards.
- **Who cannot escalate:** the requester, beneficiary and policy maker; anyone who decided on this request, as actor or principal; and anyone on the list of an open or pending stage on it. An escalator cannot decide on this request afterwards.
- **Who can be replaced:** before the deadline, only people who can no longer act; anyone else on the list must stay, including someone who has approved. After an Escalate timeout, anyone can be replaced. Someone dropped after deciding on a stage cannot be listed on it again.
- **Deadlines on escalation:** a stage past its deadline with a Reject or Cancel timeout cannot be escalated. Before that it can, but keeps its original deadline. An Escalate-timeout stage gets a new deadline.
- **Earlier approvals** still count for minimum-N after their approver is dropped (DL-046). For ALL, only the new list counts, it cannot be smaller than at opening, and it must include someone who has not yet approved.
- **Who chooses the approvers is not settled, and this blocks any real use (DL-048):** once a stage is held, one person who passes these checks can choose the approvers of every stage on a request, including mandatory and independent ones. These rules stop one person holding both duties. They do not stop an escalator and an approver colluding, and authenticating the actor alone will not close this.
- **Several stages expiring together:** Reject wins, then Cancel, whatever the stage ids. Each stage records its own timeout, and every stage whose Reject or Cancel timeout fired ends in that state. A hold changes no other stage's clock (DL-045).
- **Reasons** cannot hold control characters, line separators, bidirectional controls or broken surrogates, and need at least one visible character. Other format characters, such as the zero-width non-joiner, are allowed inside text. Cancel, Withdraw, Return and Escalate reasons are kept in `Closure` and `Escalations`, and a task's note in `Decisions`, not in the history. `ToString` on the binding, decisions, events, escalations and closures shows no people or reasons; this does not protect against serializing their properties, which the later logging layer must handle.
- **Time:** values before 2000 or from 2200 are refused, which stops one bad timestamp freezing an instance and rules out deadline overflow. A wrong time inside that range can still freeze one, with no recovery, so `now` must come from the server clock and the calling layer needs a clock-skew check.
- **Deadlines:** a decision at or after the deadline is refused even if the timeout has not been processed. A route with no applicable stage is refused, so a request can never be approved by having nothing to approve it.
- **Overrides (IAM-WF-010):** may add stages, or remove or replace non-mandatory ones. A replacement keeps its place in the order, and removing a stage while adding the same id is refused. Mandatory stages cannot be removed, replaced or marked not applicable.
- **Limits** are provisional: 64 stages, 256 candidates per stage, 1,000-character reasons, 10-year deadlines, 8 escalations per stage. The spec sets none.

Not done, tracked in R-032:
- **Concurrency:** ADR-0005's "exactly one outcome" for two real concurrent decisions needs the database.
- Storage and the audit writer, so evidence is not yet retained or tamper-evident (IAM-WF-009).
- Approver resolution, workgroups and setting up delegations (IAM-WF-003, -011): candidates are passed in already resolved.
- Reevaluation (IAM-WF-012), notifications, outbox events (P0-010), SLA numbers (DL-010) and a scheduler to apply timeouts.
- Binding to the S2-01 evaluator: `Policy.Contracts` is empty, so the binding takes plain ids and a hash. Nothing checks them against a real policy yet.
- Authorization of who may decide, escalate or cancel is the calling layer's job; eligibility here is not authentication. Domain types must not be bound directly from requests.
- Correlation ids on history and decisions (CLAUDE.md rule 9). Approvers who leave after their list was fixed stay eligible until approver resolution exists.

Results (local, Windows, 2026-11-09; not yet run in CI):
- `dotnet restore IAM.slnx --locked-mode`: exit 0. `dotnet build IAM.slnx -c Release --no-restore`: 0 warnings, 0 errors. `dotnet format IAM.slnx --verify-no-changes --no-restore`: exit 0.
- `dotnet test --solution IAM.slnx -c Release --no-build --filter-not-trait "Category=DatabaseParity"`: 750 passed, 0 failed, 0 skipped. 163 of these are `IAM.Workflow.Tests`. Database parity was not run: this story adds no storage.
- Independent read-only reviews, two rounds. The first found that an escalation could reach `ProvisioningAuthorized` without the configured people deciding; all 15 sampled defects were reproduced by running them against that code. The second found no way past the quorum counts, but 12 of 13 sampled findings reproduced, including a forced hold letting an approver replace a co-approver. All are fixed except DL-048, which stays open, and the 13th was refused for at least one character and is now tested per character. The fixes from the second round have not been independently re-reviewed.
- Mutation checks: 146 deliberate changes, one at a time, in a one-off script that is not in the repo and not part of CI: 145 caught, 1 missed, 0 inconclusive. The run has analyzers off; afterwards one parameter's type changed to satisfy the analyzers, with no change in behaviour, and the run was not repeated. The miss is the quorum recount in `ProvisioningAuthorized`: a backstop for a state no known sequence of actions reaches, so no test can trigger it. **Mutation checks only test rules that exist:** an earlier run caught 68 of 68 while the escalation defects were present.
- Generated sequences use fixed seeds (3,000 and 500 rounds), and include overrides that add, remove and replace, not-applicable stages, cancels, withdrawals, delegated rejects and non-UTC times, with minimum counts for reaching each main outcome and rule: this is sampling, not proof of every possible order.

## Action levels, authorization, Return for changes and the same approver (stage 1 of AR-1 to AR-3)
Extends the proof of concept above: same classes, still no tables, endpoints, events or packages. **Not production-ready.** Decided in DL-055 to DL-058.

- **Action levels** carry out a task, with no decision (spec 7.3). `CompleteAction` finishes one, and it can't be approved or rejected. One person does it. Timeouts can only escalate or cancel. The same people are excluded as for a decision, and finishing a task counts for independence and bars that person from escalating. A task is recorded as `Completed`, never as an approval, and doesn't appear in `Approvers`. A mandatory level can't be an action level.
- **A route needs a decision:** one with only action levels, or whose only decision level doesn't apply, is refused.
- **Authorization** comes after the last applicable decision level, so manual work can be authorized before it is done. On a route with action levels, `InstanceAuthorized` records that moment once, whoever acts first; a route with none goes straight to `InstanceApproved`, with its history unchanged. `ApprovalState.Approved` means every applicable level is approved or completed.
- **After authorization:** the requester, beneficiary and policy maker can't cancel, Withdraw and Return are refused, and a hold on one task doesn't stop others being recorded. A cancel, by a person or a timeout, makes `ProvisioningAuthorized` false and lists the tasks still open in `TasksWithUnknownOutcome`. **Undoing work already done is not built:** that is for the Provisioning story.
- **Tasks before the last decision** are allowed, for example correcting a name, but **must not change access** (DL-056). The state machine can't enforce this, because it doesn't know what a task does. The Provisioning story must: only work after authorization may touch a target. A request can be rejected after such a task, leaving it `Completed`.
- **No level is ever skipped (DL-057b).** A level with nobody able to act holds, as it did before stage 1. Stage 4 will route it to a configured fallback group, by default IAM Manager. The Skip setting built earlier in this stage was removed, which changes AR-2.6, DL-055(c), DL-056(e) and DL-056(f).
- **The same approver at the next level (DL-057c, DL-058):** when a decision level opens, it is approved for anyone who made a real decision themselves on the previous applicable decision level and is listed and eligible here. It's recorded as `ApprovedAutomatically` with a fixed remark and a link to that decision, and counts as one approval, so ALL and minimum-N still need the others. It never applies:
  - to a mandatory or independent level, or when a level is reopened by escalation;
  - from a delegate's decision, a task, or another automatic approval, so it doesn't chain beyond the next level;
  - from a level that was escalated, so an escalator can't shape a later level;
  - across a task: an applicable action level in the previous step or between the two decision levels stops it, so the person looks at what the task did. Levels that don't apply are passed over.
- **This changes earlier behaviour:** a person who decided a level and is listed on the next ordinary level used to be asked again; now they aren't. Where that never happens, routes behave as before, with tests for six route shapes.
- **Return for changes** closes the instance as `Returned`, with a required reason, and authorizes nothing. Anyone able to act at an active level may use it, including at an action level, as may their delegate, under the same checks as a decision. It is refused after authorization, during a hold, and while any stage's Reject or Cancel timeout is overdue. **Starting a new instance from a returned one isn't built:** that is for the Request story. This class only guarantees nothing carries over.

Details decided in the build, for review:
- **An empty candidate list** is refused when the route is built, as before. Stage 4 resolves approvers when a level is reached, and should settle how an empty role is handed to the fallback group then.
- **Parallel levels:** if the previous step had parallel levels, an approval on any of them carries, once per person. Carrying can't hold an independent level beside it: the person's own earlier decision has already made them ineligible there.
- **Decision ids:** a carried approval gets an id made from the original decision and the level (an XOR), so the same actions give the same result. If it would clash with an id already used, or be empty, nothing is carried and people decide as usual. A caller can choose ids that block a carry this way; it fails safe, but leaves no event explaining it. For storage: decision ids should be server-generated, with carried ids in their own namespace (R-032).
- **Who a carried approval names:** both the record and the history event name the person as actor, though they did nothing at that moment; only the outcome and event kind tell them apart. Anything counting "actions by a person" must filter on those, or storage should record no actor (R-032).
- **A carry uses up the person's say at that level:** they can't then reject or return there.

Not done:
- Resolving application roles (stage 4), so candidates are still supplied when the route is built, not when a level is reached as AR-2.6 asks.
- **Routing an empty level to the fallback group** (DL-057b) and **who may escalate** (DL-057a, a permission plus a second person on mandatory or independent stages) need approver resolution and the permission system (stage 4, P0-013).
- Editable fields (stage 5). DL-048 still blocks any real use, and this stage adds to it (see DL-048).

Results (local, Windows, 2026-11-10; not yet run in CI):
- `dotnet restore IAM.slnx --locked-mode`: exit 0. `dotnet build IAM.slnx -c Release --no-restore`: 0 warnings, 0 errors. `dotnet format IAM.slnx --verify-no-changes --no-restore`: exit 0.
- `dotnet test --solution IAM.slnx -c Release --no-build --filter-not-trait "Category=DatabaseParity"`: 827 passed, 0 failed, 0 skipped. 240 of these are `IAM.Workflow.Tests`. Database parity was not run: this stage adds no storage.
- Independent read-only reviews, two rounds. The first found no sequence wrongly reaching `ProvisioningAuthorized` or `Approved`, but did find problems with evidence, availability and rules not yet decided; 17 sampled findings all reproduced. The second, after DL-057, again found no way past the quorum rules, and 7 sampled findings all reproduced, including an approval a level ended up not counting being carried to the next. All are fixed, decided in DL-058, or recorded in R-032. **The DL-058 changes have not been independently re-reviewed.**
- Mutation checks: 233 deliberate changes, one at a time, in a one-off script that is not in the repo and not part of CI: 228 caught, 5 missed, 0 inconclusive, run against the final code. The 5 can't change behaviour today: two backstops in `DecisionsComplete` for states no known sequence of actions reaches; two checks in `CarryApprovals` kept to state the rule (an independent level, and a level that isn't active); and counting action levels as the previous step, which the task rule (DL-058c) already stops.
- Generated sequences use fixed seeds (3,000, 1,500 and 500 rounds), and include action levels, Return, delegated tasks, automatic approvals and a check at binding, with minimum counts for reaching each main outcome and rule. Their checks confirm each automatic approval against the route and history independently of the code, and that a stage closes only after an approving decision since it last opened or was escalated. Automatic approvals are rare in random routes, so their floor is low (15); the targeted tests cover them directly. This is sampling, not proof of every possible order.

## Waiting for approvers (stage 4 slice 2, DL-069, ADR-0027)
- People are `PersonId` sign-in ids, not Guids (DL-069a).
- A stage carries an approver source instead of a list. When its step opens it is `AwaitingApprovers`: its deadline runs and its timeout applies, but nobody can decide, return or escalate it until `SupplyApprovers` gives it a checked resolution. A resolution that found nobody, or a source with no data, holds it. Carrying the same approver happens when the people are supplied.
- A stage whose people are all excluded now holds without opening for anyone (it used to open and hold at once); `StageView` shows the source and the resolution in place of the configured list.

## Open questions and approvals
- Module 7.3 timeout actions "Next level" and "Auto-approve (non-access only)" do not exist in the state machine (DL-013). The spec owner should align Module 7.3.
- Escalation SLA numbers and reevaluation triggers (K.7, L.6 — DL-010 still open for these).
- **Who chooses the approvers (DL-048): blocks any real use.** Should the requester on an ALL list reduce the people required (DL-044)? Behaviour during a hold (DL-045). Other review questions (DL-046).
