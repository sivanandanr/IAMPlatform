# Decision Log — Phase 0

Status: Working log | Created: 2026-11-07 (S1-01) | Owner: Lead Development Coordinator (named owner TBD)

Purpose: record every open, conflicting or pending decision with its sources, options and owner. **An entry is never "Decided" without a named human approver and date.** Agents may propose and recommend; they may not decide (CLAUDE.md: "Where sources disagree, record a question and do not silently choose").

Sources: Spec = `docs/requirements/IAM_Portal_Functional_Specification_Final_v3_2_Dynamic_Attribute_Matrix.docx`; Blueprint = `docs/requirements/IAM_Phase_0_Development_Blueprint_v1_0.docx` (Status: "Proposed architecture baseline — requires technical and security sign-off").

Status values: **Open** (needs decision) · **Proposed** (recommendation recorded, awaiting approval) · **Decided** (named approver + date) · **Superseded**.

## 1. Spec decisions already recorded in the spec (for traceability)

| ID | Decision | Spec status | Note |
|---|---|---|---|
| DEC-001 | Birthright automatic grants without per-user approval (policy-authorized only) | CONFIRMED in H, J, K, L.6 | Register A shows OPEN; later sections state they supersede. See DL-025. |
| DEC-002 | No entitlement backup owner; workgroup escalation | CONFIRMED | Original question #1 (escalation workgroup in 1.5) still listed — DL-031. |
| DEC-003 | Maker-checker for sensitive provisioning-policy and approval-matrix changes | CONFIRMED | ADR-0009 |
| DEC-004 | Separate break-glass with independent post-use review | CONFIRMED | ADR-0008; out of Sprint 1 |
| DEC-005 | SCIM 2.0 later release | CONFIRMED (A shows DEFERRED) | Not Phase 0 |
| OPEN-001 | Owner succession SLA, thresholds, role-specific approval matrices | Pending | DL-010 |
| OPEN-002 | Retention, legal hold authority, risk scores, NFR/SLO targets, recovery objectives | Pending | DL-023, DL-029 |

## 2. Open decisions

| ID | Question | Sources | Options / recommendation | Blocks | Owner (role) | Status |
|---|---|---|---|---|---|---|
| DL-001 | Tenancy model | Spec global rule, §12.11, IAM-FR-023/025/026, FR-SEC-102 vs Blueprint §8 | Deployment-per-customer / TenantId + RLS / both. No recommendation — business decision. ADR-0014 | S1-06 | Product owner, Security Architect | Open |
| DL-002 | Module list and naming | CLAUDE.md vs Blueprint §4/§5/§6 | Rec: CLAUDE.md 13 modules, "Request", Administration owns configuration, Lifecycle only if confirmed. ADR-0011 | S1-02 | Lead Solution Architect, Functional Lead | Proposed |
| DL-003 | Browser session model and YARP placement | CLAUDE.md (BFF preferred), Blueprint §3 (YARP JWT validation) | Rec: ASP.NET Core BFF + separate BFF/YARP host. ADR-0012, ADR-0013 | S1-05 | Security Architect | Proposed |
| DL-004 | Identity provider(s); local test IdP | Spec §12.1 (any IdP, routing), Blueprint §3 | Entra ID or other; multi-IdP in Phase 0?; Keycloak locally. ADR-0012, ADR-0023 | S1-05 | Security Architect, Enterprise IAM owner | Open |
| DL-005 | AttributeDefinition owner | Spec L.4 vs Blueprint §6 vs traceability template | Rec: Identity module / `identity` schema. ADR-0015 | S1-08 | Lead Solution Architect | Proposed |
| DL-006 | EESubgroupCode normalization — is "01" equal to "1"? | Spec L.1/L.3/L.7 (unquoted `IN (1,…,6)`) vs IAM-WF-013 + Blueprint §9 (string codes) | Rec: exact string match, no zero-stripping, no numeric coercion; per-attribute normalization rule declared in catalog | S1-08 | HR data owner / attribute source owner | Proposed |
| DL-007 | Maker-checker for AttributeDefinition activation in Sprint 1 | IAM-WF-019, FR-POL-101 | Build in S1-08, or keep activation locked until built | S1-08 | Security Architect, Business owner | Open |
| DL-008 | Policy AST complexity bounds | Spec L.6 (open), Blueprint §2, §17 | PROPOSAL: depth ≤4, conditions ≤32, IN values ≤256, literal ≤256 chars, AST ≤32 KB, rules/version ≤1,000, eval ≤250 ms → HOLD. ADR-0004 | P0-008 | Business owners, Security/Risk | Proposed |
| DL-009 | Null/stale semantics in rule evaluation | IAM-WF-018 | PROPOSAL: missing/stale → UNKNOWN → HOLD, including NotEq/NotIn; never wildcard; never fall through. ADR-0004 | P0-008 | Security Architect, Business owners | Proposed |
| DL-010 | Workflow operational thresholds | K.7, L.6, OPEN-001 | Quorum, duplicate approver across stages, escalation SLAs, reevaluation triggers, approver-attribute default (beneficiary), freshness SLAs | P0-008/009 | Business owners, Security/Risk | Open |
| DL-011 | Matrix precedence | K.1/K.4/K.7 ("specificity then tie-break priority") vs L.3 ("more-specific conditions do not override a higher-priority rule") | Needs single rule; "specificity" undefined for OR/nested rules | P0-008 | Business owners, IAM Domain lead | Open |
| DL-012 | JobGrade data type | IAM-WF-015 (`JobGrade >= 7`), L.3 | Integer vs code ("07") | P0-008 | HR data owner | Open |
| DL-013 | Timeout actions "Next level" and "Auto-approve (non-access only)" | Spec Module 7.3 vs K.1, FR-001F, CLAUDE.md rule 3 | PROPOSAL: disallow both on mandatory and access-granting stages; define "non-access action". ADR-0005 | P0-009 | Security Architect, Business owners | Proposed |
| DL-014 | Simulation scope in Phase 0 | IAM-WF-008 (P0), IAM-FR-001G vs IAM-WF-021 (P1) | Single-request simulation in P0; bulk impact P1? | P0-008 | Functional Lead | Open |
| DL-015 | Workflow selection order | Spec Module 7.2 (application > connector type > criticality > account type > default) vs K.1 (entitlement > application > request type > enterprise default) | K states it supersedes; confirm | P0-009 | Functional Lead | Open |
| DL-016 | Maker-checker for role/permission changes in Phase 0 | Spec §12.2 ("permissions are data", second person) | Include in P0-013 or defer | S1-05 / P0-013 | Security Architect | Open |
| DL-017 | Frontend workspace and styling | UI_FOUNDATION_FIRST / COMPONENT_ARCHITECTURE (`shared/ui`) vs Frontend Architect rec (`projects/iam-ui`); Blueprint §3 (Angular 20+, Tailwind); Spec "Clarity styling" | Layout A/B; Angular pin (newest supported); Tailwind major; CDK yes/no; AG Grid Community/Enterprise; Clarity = visual reference only? ADR-0019 | S1-09 | Frontend Architect (not self-approving), Security Architect, QA | Open |
| DL-018 | Package and licence approvals | CLAUDE.md dependency rule | MassTransit v8/v9 (ADR-0021); MediatR/AutoMapper (ADR-0020); FluentAssertions v8 (ADR-0022); Duende; Redis vs Valkey; k6 (AGPL); Docker Desktop subscription; SQL Server Developer edition (non-prod); EFCore.NamingConventions; NetArchTest/ArchUnitNET; Testcontainers; Vitest; Playwright; axe-core; Tailwind; Keycloak; gitleaks | S1-02 onward | Procurement / Legal, Lead Solution Architect | Open |
| DL-019 | Supported database versions | Blueprint §7, Spec H | Azure SQL MI update policy; minimum on-prem SQL Server; PostgreSQL 17 vs 18; Npgsql EF Core 10 version | S1-06 | DBA lead | Open |
| DL-020 | Migration recovery approach | Blueprint §7 ("forward-only") vs database-architect role ("forward and recovery migrations") | PROPOSAL: restore/PITR + compensating forward migration. ADR-0003 | S1-06 | DBA lead | Proposed |
| DL-021 | Outbox/inbox table location | Blueprint §6 (`integration` schema) vs CLAUDE.md (no cross-module writes) | Building-block tables in `integration` vs per-module outbox/inbox. ADR-0006 | S1-06 | Lead Solution Architect, Database Architect | Open |
| DL-022 | Audit write atomicity | IAM-FR-027 ("acknowledged audit records are never lost") | Rec: in-transaction audit writer. ADR-0018 | S1-07 | Security Architect | Proposed |
| DL-023 | Retention and legal hold | Spec §12.10 (periods 60/90/180/365/730 days) vs OPEN-002 / H (pending); original question #7 | Approve periods and legal-hold authority | Later (purge design) | Compliance / Legal / Records | Open |
| DL-024 | Requirements `.docx` in git; public repository | Repo is public (github.com/sivanandanr/IAMPlatform); CLAUDE.md hygiene | Make repo private, or keep specs out of git; `Claude Setup.exe` deletion | Ongoing | Repository owner | Open |
| DL-025 | DEC-001 register inconsistency | Spec register A (OPEN) vs H/J/K/L.6 (CONFIRMED) | Spec owner to issue one corrected register | — | Spec owner | Open |
| DL-026 | Dashboards via "approved, read-only stored procedures" | Spec §12.4 vs Blueprint EF/Dapper approach | Per-provider procedures vs per-provider Dapper views | Later | Database Architect | Open |
| DL-027 | Search `*` "shows everything" | Spec (search behaviour) vs bounded results / disclosure controls | PROPOSAL: first server-paged, authorization-scoped page only | S1-09 / later | Security Architect, Product owner | Proposed |
| DL-028 | UI foundation absent from blueprint backlog | CLAUDE.md UI gate vs Blueprint §14/§15 | Extend Phase 0 (~6 weeks) or make P0-018 demo API-level | Phase 0 plan | Product owner, Lead Solution Architect | Open |
| DL-029 | NFR / SLO / RTO / RPO targets | IAM-FR-027, FR-REL-102, OPEN-002, Spec G (proposed targets) | Approve or revise proposed targets | P0-019 | SLO owners (unnamed in spec) | Open |
| DL-030 | Encryption of sensitive attribute values | IAM-WF-020; Database Architect | Always Encrypted vs pgcrypto/app-level; impacts indexing per engine | Later | Security Architect, DBA lead | Open |
| DL-031 | Escalation workgroup step in ownership succession (§1.5) | Original question #1 vs DEC-002 | Confirm whether the step stays | Later | Functional Lead | Open |
| DL-032 | UUID strategy | Blueprint §7 | Rec: app-generated UUIDv7; SQL Server clustering caveat. ADR-0016 | S1-06 | Database Architect | Proposed |
| DL-033 | Component inventory vs UI foundation list | COMPONENT_INVENTORY (TextArea/DateRange/MultiSelect P1; `IamTextInputComponent`/`IamSelectComponent`) vs UI_FOUNDATION_FIRST UI-1 (`textbox`/`dropdown`, textarea and date-range required) | Reconcile priority and naming | S1-09 | Frontend Architect | Open |
| DL-034 | Depth of auth in Sprint 1 | Blueprint Week 1 exit ("secure sample endpoint") vs P0-003 ("OIDC login") | Full BFF OIDC login in Sprint 1 (planned) vs secured endpoint only | S1-05 | Security Architect | Open |

## 3. Change history
| Date | Change | By |
|---|---|---|
| 2026-11-07 | Initial log from Phase 0 Sprint 1 planning reviews (Functional Lead, Solution Architect, IAM Domain, Database, Security, Frontend) | Claude Code (S1-01), pending human review |
