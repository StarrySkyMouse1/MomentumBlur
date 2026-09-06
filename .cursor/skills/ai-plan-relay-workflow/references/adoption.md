# Adoption

## Copy as an on-demand skill

Copy the entire `ai-plan-relay-workflow` directory into a skill directory supported by the target agent, for example:

- `.codex/skills/ai-plan-relay-workflow/`
- `.agents/skills/ai-plan-relay-workflow/`
- `.cursor/skills/ai-plan-relay-workflow/`

Keep the directory intact so `SKILL.md`, `agents/openai.yaml`, and `references/` remain together. The source repository
does not need this skill registered in order to distribute it.

## Optional project gate

Only when the target project wants Relay behavior enforced on every applicable request, add this project-specific rule
to its own instruction file:

> When the user asks for an execution plan or asks another AI to implement the work, use `$ai-plan-relay-workflow`.
> Inspect the current repository before planning, keep durable rules in the accessible skill and project instructions,
> send only a delta task packet, use the fewest reliable capacity-sized rounds, and review each macro-stage receipt
> before advancing.

Do not copy source-project branch, framework, test, commit, or deployment rules. Add those from the target project's own
instructions.

## Adaptation checklist

- Confirm the target agent recognizes its chosen skill path.
- Preserve `name: ai-plan-relay-workflow` unless every invocation and gate is updated consistently.
- Keep automatic discovery unless the target user explicitly requests explicit-only invocation.
- Add target-project rules to its instruction files, not to this reusable skill.
- Test with realistic Relay requests and verify that ordinary coherent work stays in one round, context-scale work uses
  only necessary macro stages, non-foundation findings advance in the ledger, and foundation blockers still stop safely.

