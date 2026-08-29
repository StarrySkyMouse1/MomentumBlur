# Workflow contract

## Context economy

- Durable governance lives in the workflow skill and project instructions; task packets reference it instead of copying it.
- Detailed architecture lives in a versioned repository plan; packets identify the exact authorized section.
- Packets, receipts, and reviews carry only facts that changed for the current stage.
- Self-contained means the cited skill, plan section, and task packet together are decision-complete.
- If a cited resource is inaccessible, include only the minimum compatibility rules needed for safe execution.

## Risk and capacity classification

Classify by the highest applicable risk, not only by file count:

| Level | Typical signals | Relay behavior |
|---|---|---|
| I0 | 1-3 production files; one local, reversible behavior | One complete executor prompt |
| I1 | 4-10 production files; one feature or existing boundary | One bounded executor prompt |
| I2 | Larger coherent feature, shared contract, cross-process path, persistence, or global state | Default to one executor round when it fits a complete implementation/self-review/validation loop |
| I3 | Context-scale work, unresolved scope, independently gated security/data boundary, or irreversible migration | Use the fewest coherent macro stages needed; review after each completed macro stage |

Project rules may impose stricter thresholds. Risk always upgrades the level.

File count is an estimation signal, not a splitting rule. Do not impose a universal per-stage file maximum. Split only
when at least one condition is true:

- one executor cannot reliably implement, inspect the full diff, validate, repair, and report within one context;
- an irreversible, data-safety, or security boundary needs independent evidence before dependent work begins;
- exact scope or a material architecture decision is still unknown;
- a temporary intermediate state would otherwise violate an invariant and must be separately controlled.

Do not create a stage solely for a read-only contract restatement, one error path, one test failure, or a small repair
that fits the next authorized scope. For I3, a few macro stages are usually sufficient, but no fixed count is mandatory.

## Global plan requirements for split work

The plan must state:

1. level and evidence for the classification;
2. verified baseline; the executor independently reads and protects the current workspace status;
3. one target strategy and explicit non-goals;
4. estimated production files, test files, boundaries, and data risk;
5. ordered stages with dependencies and final acceptance;
6. exact scope for the currently authorized stage;
7. create, modify, delete, and retain decisions;
8. entry conditions, validation, evidence, and stop conditions;
9. task-specific permission exceptions not already fixed by the project skill;
10. failure and insufficient-evidence behavior;
11. a decision packet containing architectural direction, invariants, state transitions, failure semantics, forbidden
    shortcuts, and the reasons behind non-obvious choices;
12. the closeout policy and the final stage that owns unresolved non-blocking findings.

Each macro stage must be sized so its implementation, self-review, validation, and receipt can complete together. If the
stage cannot be expressed precisely, authorize one discovery round. Once discovery freezes the strategy, scope, and
acceptance target, later implementable edge cases become execution requirements rather than new discovery stages.

## Scope rules

- List exact file paths. Terms such as "related files", "all modules", and "as needed" are not write authorization.
- Separate production files, test files, documentation, generated files, and external systems.
- State whether files may be added, deleted, renamed, or only modified.
- Identify known pre-existing dirty files and how the executor must preserve them.
- Do not authorize future-stage cleanup or opportunistic refactoring.
- If an unexpected file is required, do not silently expand scope. Continue safe in-scope work and record it for the next
  macro stage or closeout; stop immediately only when the missing file creates a foundation blocker.

## Evidence rules

- Prefer observable product behavior, repository diffs, existing tests, static checks, builds, and logs already allowed by the project.
- Select validation by failure risk. Cover changed behavior, important error paths, boundary contracts, persistence or
  migration safety, and the build or static checks needed to detect integration breakage. Do not run a token-heavy full
  suite when narrower evidence is sufficient, but do not use a lightweight check to claim a high-risk contract passed.
- State test-file authorization separately from permission to run tests. When test changes are authorized, list their
  exact scope and expected assertions. When they are not authorized, the executor must report the required change rather
  than silently rewriting tests.
- Never add production-only probes, counters, events, APIs, UI, delays, or debug switches solely to make a test pass unless explicitly authorized as product behavior.
- Report skipped or unavailable evidence honestly. Missing evidence is not a pass.
- Preserve raw command, exit-code, and failure information needed for independent review.

## Executor completion loop

Before returning a stage, the executor must perform these passes:

1. **Direction pass:** compare the implementation to the decision packet, architecture boundaries, invariants, state
   transitions, and forbidden shortcuts.
2. **Behavior pass:** inspect success, empty, boundary, failure, retry/rollback, compatibility, and cleanup paths that
   are relevant to the change.
3. **Diff pass:** inspect the complete diff for accidental edits, duplicate strategies, dead code, missing deletion,
   user-change overlap, and files outside authorization.
4. **Validation pass:** run authorized risk-proportionate checks and preserve the evidence.
5. **Acceptance pass:** evaluate every acceptance row as pass, fail, or unverified.

The executor must repair findings that are within the authorized objective and file scope, then repeat the affected
passes. Return to the reviewer only when the stage is ready or a mandatory stop applies. This is a bounded self-correction
loop, not permission to redesign the plan.

## Finding severity and forward progress

Classify findings relative to the current stage's purpose. Planning asks whether a safe, decision-complete execution
prompt can be issued; implementation asks whether the produced foundation is safe for dependent work. Do not fail a
planning stage for an error path that already has one clear solution inside the approved implementation scope.

Classify findings by consequence:

### Foundation blocking

Do not advance only when the finding makes further execution unsafe or false: an unresolved architecture choice or wrong
direction, credible data-loss/security/irreversible risk, an untrusted repository baseline, scope that cannot yet be
bounded, a required foundation contract that dependent work cannot safely bypass, or required evidence proving the
foundation unusable. These are the only reasons to issue a same-stage repair prompt.

### Carry-forward

A finding moves forward when it is understood, has one treatment compatible with the frozen strategy, can be assigned
exact scope and acceptance, and does not make the next macro stage unsafe. It may include incomplete behavior or failed
local validation that the next authorized work can repair; it need not be cosmetic. Carry-forward is an explicit Codex
decision, not the executor hiding a failure.

### Closeout-only

Cosmetic consistency, localized cleanup, documentation correction, or other bounded polish may be deferred to the final
closeout stage when it cannot compound or obscure later verification.

Every deferred finding enters a closeout ledger with: stable ID, severity, evidence, user-visible or engineering impact,
exact scope, intended owner stage, acceptance check, dependencies, and the condition that upgrades it to blocking.
Do not defer the same failed item repeatedly. If its owner stage cannot close it, if it spreads, or if its assumptions
become uncertain, upgrade it to blocking and repair before advancing.

The reviewer should combine carry-forward work with the next macro stage by default. Do not create a repair-only round
unless it is foundation blocking. When main execution is complete, route the accumulated ledger as follows:

- Codex may directly close 1-3 production files when the work is local, reversible, within the approved objective, and
  does not touch persistence, security, irreversible data, or external side effects.
- Otherwise issue one consolidated executor closeout prompt covering the exact ledger and interaction checks.
- Codex always performs final independent acceptance. Final acceptance requires an empty ledger or an explicit user
  decision to accept documented residual debt.

## Frozen target and incremental review

After the first review freezes architecture direction, authorized scope, non-goals, and acceptance criteria:

- later reviews inspect the actual delta, inherited findings, validation, and effects on dependent work;
- newly noticed in-scope edge cases are appended to the next execution acceptance matrix;
- do not restart broad discovery or move the target unless new repository evidence directly contradicts it;
- one discovery rework is normally enough. On a later review, Codex must integrate resolvable findings and advance, or
  identify the concrete foundation blocker that makes advancement unsafe.

## Relay state model

- **Plan ready:** direction, execution rounds, current exact scope, and final acceptance are frozen.
- **Executing:** the executor implements the current round and completes its bounded self-correction loop.
- **Review:** Codex returns Pass, Advance with closeout, or Fail. Pass and Advance both move the plan forward; Fail means
  Foundation blocked and is the only state that repeats the same round.
- **Closeout:** after main execution, the accumulated ledger is resolved by Codex or one executor round using the routing rule.
- **Final acceptance:** Codex independently checks the completed objective, validation, diff, and empty or user-accepted ledger.

## Mandatory stop conditions

Stop without advancing only when any applies:

- the authorized stage is complete;
- a foundation-blocking acceptance condition fails or lacks evidence;
- an unauthorized file, boundary, migration, or external action is required and its absence makes further work unsafe;
- the verified repository baseline contradicts the plan;
- pre-existing user changes cannot be safely separated;
- remaining context is insufficient for implementation, review, and a truthful receipt.

Non-foundation findings do not trigger a hard stop: finish safe in-scope work, record them, and return a complete receipt.
Stopping still requires a complete stage receipt. Failure must not be presented as completion.
