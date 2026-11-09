# ADR-0004: Typed metadata-driven policy engine (attribute catalog + typed AST)
Status: Proposed, proof of concept implemented (S2-01, revised after independent review; not production-ready) | Date: 2026-11-07, updated 2026-11-09 | Decision owners: IAM Domain / Workflow lead, Security Architect, Business owners for thresholds (named approvers TBD)

Source: Blueprint v1.0 §1 (ADR 004), §9; Spec v3.2 IAM-WF-013, -014, -015, -016, -018, -022, Appendix L.1–L.6; CLAUDE.md rules 2 and 8. Drafted by Claude Code (S1-01); not approved.

## Context and requirements
Approval matrices and birthright policies must use a registered typed attribute catalog (not fixed Company/Department/EESubgroupCode columns). Adding a new mapped catalog attribute must not require a schema migration (IAM-WF-013; blueprint §16). Administrator-authored SQL, C#, JavaScript or arbitrary expressions must never execute (CLAUDE.md rule 8).

## Options considered
1. Registered `AttributeDefinition` catalog + typed JSON AST with an allowlisted operator set, evaluated in-process (proposal).
2. Expression language (e.g. dynamic LINQ, JS, CEL) evaluated at runtime.
3. Fixed columns per known attribute.

## Decision and rationale
Option 1.
- **AttributeDefinition** fields: stable Id, immutable Key, DisplayName, DataType (String/Code, Integer, Decimal, Date, Boolean), SubjectScope, AuthoritativeSource and priority, NormalizationRule, NullHandling, Sensitivity, Freshness, AllowedOperators, Status, Version, Maker/Checker.
- **AST nodes**: `group` (AND/OR, nested), `condition` (subject, attributeId, operator, values), explicit `wildcard` only where a rule allows it.
- **Operators**: String/Code → Eq, NotEq, In, NotIn, IsEmpty, IsNotEmpty (no ordering). Integer/Decimal/Date → those plus Gt, Gte, Lt, Lte. Boolean → Eq, IsEmpty, IsNotEmpty. No regex / StartsWith / Contains. New operators are engineering changes.
- **Literals** are JSON strings parsed against the attribute's DataType; no implicit coercion. `attributeId` is authoritative; `attributeKey` is refused (see the proof of concept below).
- Unknown properties, duplicate JSON keys, unknown/inactive/unauthorized attributes, type-invalid operators and empty groups → refused. The proof of concept gives a stable rejection code for a later 422, with no endpoint yet. It refuses an attribute not in the catalog it is given; that catalog is trusted to hold published attributes only. Checking the caller's right to use an attribute is **not implemented**.
- **Text attributes are refused in rules** (DL-040, decided 2026-11-09): comparing free text cannot tell a different value from the same value written differently (Unicode forms, look-alike characters, blanks). The catalog can still store them.
- **Null semantics (DL-009, decided 2026-11-09)**: three-valued logic. Any comparison on a missing, stale, invalid or conflicting value (including NotEquals/NotIn) is UNKNOWN; `true OR unknown` is true and `false AND unknown` is false. UNKNOWN at a priority level → HOLD. Missing data is never a wildcard and never falls through to a broader rule (IAM-WF-018).
- **Precedence (DL-011, decided 2026-11-09)**: priority only. Levels are checked from the highest down: two or more true → HOLD (ambiguous); any unknown → HOLD, even beside a true rule; one true → match; all false → next level; nothing left → HOLD (no match).
- Published versions are compiled/cached; decision evidence includes policy version and hash (blueprint §9).

## Security and privacy impact
No code execution from configuration. The limits bound the work one evaluation can do; they do not limit how often it runs, which is for the later API. Sensitive attribute values are left out of evidence, though a condition's result combined with the published rule's literal can still reveal one (`Truth=True` on `Equals "L9"`), so access to evidence and rules must be restricted when they are stored (IAM-WF-020, R-031).

## SQL Server vs PostgreSQL impact
AST stored as JSON text (`nvarchar(max)` + `ISJSON` CHECK / `jsonb`) behind read adapters; query-critical metadata (attribute ids, rule priority, status) in indexed columns. Hashes computed over application-canonical bytes, never over a `jsonb` read-back (ADR-0017).

## Hybrid deployment impact
None.

## Operational consequences and rollback
Changing operator semantics requires a new AST `schemaVersion`; old published versions remain evaluable.

## Tests / evidence
Expected: EESubgroupCode registered with no migration; "01" vs "1" handled per DL-006; numeric literal for a Code attribute refused; Gt on Code refused; limits exceeded refused; missing/stale → HOLD; replay of the same snapshot → same result. Registering with no migration and the 422 responses are **not demonstrated**: no publish path or endpoint exists yet. The rest is covered by the results below.

## Proof of concept (S2-01, P0-008)
Pure code in the Policy module, in `Domain/Rules`: `RuleDocumentParser`, `PolicyCompiler`, `PolicyEvaluator`, `RuleOverlapAnalyzer`, `AttributeValueCodec`, plus `Application/Rules/AttributeSpecMapper` for the Identity contract. No tables, endpoints, events or packages, so database parity does not apply yet. **It selects a rule; it grants and approves nothing, and nothing calls it yet.**

Internal by design: the compiled types can only be created by `PolicyCompiler` and cannot be altered afterwards, so a hash always describes its rules. The parser, nodes and value parser are internal to the module.

Details decided in the build, for review:
- **Document:** `{"schemaVersion":1,"root":node}`; node is `group` (`operator` And/Or, `children`), `condition` (`subject`, `attributeId`, `operator`, `values`) or `wildcard` (`subject`, `attributeId`). Names match the catalog's exactly. Priority, effective period and wildcard permission are typed fields of the rule, never read from the document.
- **`attributeKey` is refused** rather than accepted as a display label: a label beside an id could make a rule look like it tests something else. This tightens the earlier wording above.
- **States:** a snapshot entry with a null value means the source recently confirmed there is none (Empty). No entry at all is NotSynchronized, which is UNKNOWN for every operator including IsEmpty, because nothing is known. A date after `asOf` counts as stale. Two entries for one attribute are Conflicted, never a choice.
- **Wildcard:** only where the rule allows it, and true only for a fresh, valid value, so a missing attribute cannot reach a broad rule.
- **Subject:** always explicit. A beneficiary-scoped attribute can be read for the requester too (spec L.7); other scopes only for themselves. Values for the two people never mix.
- **Limits:** the DL-008 numbers, provisional. Time is a step budget, not a clock, so replay is identical on any machine. The budget is exactly what a maximal version needs (129,000 nodes), so it is a guard that a compiled version cannot reach; the limits themselves bound the work. It is not caller-adjustable. The 250 ms proposal is measured, not enforced (below).
- **Hash:** SHA-256 over this code's canonical bytes, covering rules, priorities, periods at full precision, literals, the exact attribute versions, and each attribute's type, scope, sensitivity, normalisation, freshness, length and operators. Authoring order and list order do not change it. A fixed test vector guards against the canonical form changing silently.
- **Evidence:** policy version and hash, attribute versions, evaluated rules and each condition's result and state. Values appear only for Public and Internal attributes. Confidential and Restricted values are left out entirely, not hashed, because hashes of short codes are easy to guess. Invalid values are never copied.
- **Overlap check (partly covers IAM-WF-016):** flags two rules at the same priority unless their periods do not meet, or an attribute constrained through AND has no value both allow (lists, exclusions and ranges on numbers, dates and timestamps). Also reports rules that can never be true. OR groups are not analysed, so it can flag a harmless pair, but it does not claim two rules differ without proof; a generated-rules test checks this against the evaluator. It cannot tell whether overlapping rules *disagree*, because rules have no route yet, and it does not report rules made unreachable by a higher one. Results over 1,000 overlaps are cut off with a flag that never reads as clean.
- **Value parsing** is a copy of Identity's rules, because Domain layers cannot share code across modules; a test compares the two on a corpus of edge cases across every type and normalisation. It uses plain character scans rather than regexes, because a regex timeout would make results depend on how busy the machine is. Codes must be ASCII before upper-casing, in both parsers, so U+017F cannot become "S".

Not done:
- Storage and hash persistence, publication and maker-checker (IAM-WF-019, DL-007), simulation API (DL-014), approver resolution, and fallback or escalation targets for holds (IAM-WF-006, IAM-WF-018 "configured" policy).
- `Policy.Contracts` is still empty, so other modules cannot call the evaluator yet.
- Catalog fields in the proposal above that the contract and engine do not have: NullHandling, Status and source priority.
- **Evidence is not yet complete for audit** (owner chose to do this with storage): no snapshot reference, no per-condition synchronisation time, no beneficiary or requester identity, no record of rules skipped as out of period. Until then an auditor cannot show why a value counted as fresh. Worst-case evidence is 6.7 MB and needs a cap or a decision when stored.

R-031 tracks these.

Results (local, Windows, 2026-11-09; not yet run in CI): `dotnet restore IAM.slnx -p:ContinuousIntegrationBuild=true`, `dotnet build IAM.slnx -c Release --no-restore` (0 warnings, 0 errors), `dotnet format --verify-no-changes` clean, `dotnet test --solution IAM.slnx -c Release --no-build --filter-not-trait Category=DatabaseParity`: 587 passed, 0 failed. Database parity tests were not run: this story adds no schema. Mutation checks: 84 changes to safety-critical code tried one at a time; 81 fail a test, and 3 cannot change behaviour because a later check gives the same answer (non-ASCII digits, a seventh fraction digit, `!Truncated` in `IsClean`). The independent security and code reviews were advisory; human Security Architect and domain approval is still required (REVIEW_POLICY).

Timing, measured once in a Release build on one development machine, not a benchmark: a maximal version (1,000 rules, 32 conditions and 97 groups each, short codes) evaluates in 48 ms median and 86 ms at worst over 30 runs, and compiles in about 2.2 s. The overlap check took 260 ms on 1,000 rules at one priority. The worst realistic overlap case was not measured.

## Open questions and approvals
Complexity bounds are **undecided in the spec** (L.6). The following are PROPOSALS needing sign-off (DL-008), used provisionally by S2-01: group depth ≤ 4; conditions per rule ≤ 32; IN/NOT IN values ≤ 256; literal ≤ 256 chars; AST ≤ 32 KB; rules per matrix version ≤ 1,000; evaluation time per item ≤ 250 ms → HOLD.
Also: JobGrade data type (DL-012), and the differences between this ADR's operator list and the catalog's (DL-041).
