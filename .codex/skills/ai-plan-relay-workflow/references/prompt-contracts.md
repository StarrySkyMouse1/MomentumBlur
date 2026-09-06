# Prompt contracts

## Rule residency

The accessible workflow skill owns durable rules: workspace protection, branch and commit policy, test permissions,
production-code purity, executor self-review, stop conditions, finding severity, ledger fields, and receipt behavior.
Do not copy those rules into each task packet.

Substantial task architecture belongs in a repository plan. Cite the exact section instead of copying the plan. A task
packet remains decision-complete because the executor reads the cited skill and plan before acting.

## Executor task packet

Generate one directly copyable block with at most these seven sections:

1. **Use skill:** exact accessible skill path; this is the first line.
2. **Task:** one-sentence outcome and stage ID.
3. **Baseline:** repository path and verified branch/revision. The executor reads the current status itself.
4. **Change scope:** exact production and test files, including create/delete decisions only when applicable.
5. **Implementation contract:** only behavior, data flow, compatibility, and failure semantics unique to this stage.
6. **Acceptance:** observable outcomes and exact validation commands.
7. **Plan reference:** exact repository document and section containing the detailed contract.

Omit empty sections. Do not repeat the skill, history, unchanged non-goals, pre-existing dirty-file inventory, generic
self-review instructions, or generic receipt instructions. Use no placeholders. If the skill is inaccessible, add only
the minimum authorization and safety rules required to execute safely.

## Compact executor receipt

The executor returns only changed facts:

```text
Conclusion: Pass candidate | Advance with ledger | Foundation blocked
Commit: hash or reason not committed
Files: actual changes
Validation: command and result summary
Deviation: none or exact delta
Ledger: only added, resolved, or changed IDs
```

Preserve raw errors only when a command failed or evidence is disputed. The executor also emits a compact return block
containing repository, stage, commit, validation, deviation, and ledger. Codex obtains the original contract from the
stage ID and plan reference and independently inspects the workspace. The user fills nothing in.

## Compact Codex review

Start with **Pass**, **Advance with closeout**, or **Fail**. Report only findings that affect advancement, ledger changes,
and the next action. Do not restate the plan, stable rules, unchanged contract, or full review procedure. If execution
continues, append one delta-only task packet for the next stage.

## Final closeout routing

When main execution is complete, first choose the owner:

- Codex directly resolves a local, reversible ledger limited to 1-3 production files with no persistence, security,
  irreversible-data, or external-side-effect boundary.
- Otherwise authorize one executor to process the entire closeout ledger in one bounded pass.

For either owner:

1. re-read current project rules and verify that each ledger item is still correctly classified;
2. resolve every item within the exact closeout scope, using the normal completion loop;
3. inspect interactions among accumulated fixes rather than treating them as unrelated edits;
4. run the final risk-proportionate acceptance matrix for the completed feature or milestone;
5. stop and escalate any item that has become architectural, risky, ambiguous, or outside scope;
6. return an empty ledger or an explicit list of residual items requiring user acceptance;
7. require Codex to perform the final independent acceptance after the fixes.

