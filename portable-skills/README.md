# Portable skills

This directory contains distributable skills that are not registered in ShuShu Workstation's active `.codex`,
`.agents`, or `.cursor` instruction chains.

## AI plan relay workflow

`ai-plan-relay-workflow/` packages the reusable workflow for:

1. Codex inspecting a target project and producing an exact execution plan;
2. another AI implementing the largest reliable coherent round rather than mechanically small stages;
3. the executor self-reviewing and repairing in-scope defects before returning a populated receipt;
4. Codex independently reviewing evidence, advancing with bounded findings, and reserving same-stage repair for
   foundation blockers;
5. a final consolidated closeout routed to Codex or the executor by size and risk.

Copy the complete skill folder into the target project's supported skill directory. Read
`ai-plan-relay-workflow/references/adoption.md` for installation and optional per-project injection.

The portable package intentionally has no dependency on ShuShu Workstation files and is not loaded by this repository's
current agents unless explicitly copied into an active skill path.
