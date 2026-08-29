---
name: ai-plan-relay-workflow
description: >
  Inspect a software project and produce precise, self-contained execution plans and prompts for another AI,
  using the fewest capacity-appropriate execution rounds with executor self-review, progressive Codex review,
  and final closeout. Use for planning, reviewing, or continuing an AI relay; not ordinary direct implementation.
---

# AI Plan Relay Workflow

Use this skill as a coordination layer. The target project's own `AGENTS.md`, repository instructions,
authorization boundaries, branch policy, test policy, and delivery rules always take precedence.

## Route the request

Classify the collaboration mode before planning:

- **Relay:** the user wants a plan or prompt for another AI. Plan and issue a prompt; do not implement unless separately authorized.
- **Review:** the user returns an executor report. Independently inspect repository evidence before accepting it.
- **Discovery:** a missing product or architecture choice would materially change the implementation. Resolve only that choice before issuing an executable prompt.

Do not activate this workflow merely because another AI is mentioned historically.

## Establish the project contract

Before producing a plan or review:

1. Read the nearest applicable repository instructions and relevant design, plan, and acceptance documents.
2. Inspect the current branch or revision, `git status --short`, relevant code, and existing validation commands.
3. Separate pre-existing user changes from the proposed scope. Never authorize cleanup, reset, stash, commit, branch, worktree, deployment, or external mutation unless the project rules or user explicitly allow it.
4. Treat executor summaries, roadmap checkboxes, and claimed test results as untrusted until verified against current evidence.
5. Keep durable rules in an accessible project or portable skill. Put only task-specific decisions in the generated prompt.

## Size and stage the work

Use the capacity model in [references/workflow-contract.md](references/workflow-contract.md). Keep work in one executor
round whenever one agent can implement, self-review, validate, repair, and report it reliably in one context. Split only
when context capacity is genuinely insufficient, an independently gated irreversible or security-sensitive boundary must
be proven before dependent work, or discovery is still required. Large work should use a few coherent macro stages, not
micro-stages created merely because files, processes, or error paths differ.

Every authorized stage must define:

- one concrete outcome;
- exact production-file and test-file scope;
- explicit create, modify, delete, and retain decisions where ambiguity is dangerous;
- entry conditions and acceptance evidence;
- authorized validation commands;
- hard stop conditions;
- a required compact executor receipt and return-to-Codex packet.

Put durable workflow rules in the skill and substantial task architecture in a repository plan. The task packet carries
only the current stage's decisions and cites the exact plan section. Give the executor implementation freedom inside
those boundaries so it can solve local details without repeatedly asking the reviewer.

If exact file scope or a material architecture choice cannot be determined from the repository, authorize discovery
only. Once the strategy, scope, and acceptance target are frozen, do not create repeated read-only contract rounds for
edge cases that can be handled inside the planned implementation scope.

## Generate a delta-only task packet

Read [references/prompt-contracts.md](references/prompt-contracts.md) before writing a task packet or reviewing a returned
stage. Output one directly copyable code block with no placeholders. Its first line must point to the accessible workflow
skill. Limit the packet to the current task delta: stage, baseline, exact scope, task-specific contract, acceptance,
commands, and an exact plan-section reference when the detailed contract lives in the repository.

Do not repeat branch policy, workspace protection, test policy, completion-loop mechanics, stop conditions, ledger
schema, or receipt rules already present in the cited skill. The executor reads and applies those rules. Only when the
execution environment cannot access the skill may the packet include the minimum safety rules needed for compatibility;
never restore the old exhaustive prompt template.

## Review a returned stage

Independently inspect the current workspace, diffs, relevant files, and validation evidence. Compare actual evidence to
the authorized stage. First classify each finding using the severity and carry-forward rules in
[references/workflow-contract.md](references/workflow-contract.md), then classify the stage as:

- **Pass:** the stage outcome is evidenced and the plan can advance without inherited findings.
- **Advance with closeout:** the plan can safely advance while bounded findings travel with the next macro stage or final closeout.
- **Fail:** a foundation blocker makes further execution unsafe or would make later work build on a false premise.

Judge severity relative to the current stage. A defect is not a planning blocker when the strategy is already unique and
the defect can be resolved inside the authorized implementation or next macro-stage scope. After the first full review,
review deltas and prior findings rather than reopening an unbounded search or moving the acceptance target.

Prefer forward progress whenever it is safe. Record non-foundation findings in a closeout ledger with evidence, owner,
exact scope, acceptance, and escalation trigger. Then provide exactly one next action:

- a same-stage repair prompt only for a foundation blocker;
- a complete prompt for the next macro stage that also carries unresolved findings;
- a final closeout action selected by the closeout routing rule; or
- a final acceptance result when no work remains.

Never advance merely because a roadmap box is checked or because the executor says the stage passed. Conversely, do not
hold the plan in place to reach local perfection when the finding can safely travel with the planned work.

## Portability boundary

This skill deliberately contains no fixed framework, operating system, branch, build, test, commit, or deployment policy.
Derive each of those from the target project. See [references/adoption.md](references/adoption.md) when installing the
skill into another project or enabling an always-on project gate.

