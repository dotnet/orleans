---
name: code-review
description: Review Orleans changes for layer contracts, invariants, minimal complexity, runtime performance, testing, and focused documentation. Use for GitHub Copilot code review and other review tasks.
---

# Orleans code review

1. Read root `AGENTS.md` and applicable directory instructions. Apply their
   implementation principles alongside standard correctness, security,
   compatibility, concurrency, reliability, and testing checks.
2. Establish the change's goal and each affected layer's required and provided
   guarantees. Trace the callers, ownership, lifetimes, and transitions relevant to
   the change to verify the contracts and distinguish proven invariants from assumptions.
3. Check that each addition in the diff (state, abstractions, work, allocations,
   retained memory, error handling, documentation) serves a concrete requirement.
   Recommend the simplest complete correction that preserves required behavior and
   contracts.
4. Examine behavioral tests and the automated PR coverage comment. Identify
   meaningful gaps or regressions in affected code; state when evidence is missing.
   Support performance claims with measurements or explicit cost assumptions.
5. Flag additional invariants that could simplify code: "If we can guarantee X,
   then we could simplify this by Y." Identify the owning layer, enforcement, and
   feasibility; distinguish conditional design suggestions from confirmed
   contract violations.
6. Review the affected code through each lens skill, reporting only material,
   in-scope findings:
   `.github/skills/review-architecture/SKILL.md`,
   `.github/skills/review-invariants/SKILL.md`,
   `.github/skills/review-testing/SKILL.md`,
   `.github/skills/review-performance/SKILL.md`, and
   `.github/skills/review-code-craft/SKILL.md`.

Report actionable findings tied to changed lines, explaining the consequence,
evidence, and correction. Prioritize correctness and meaningful runtime or
maintenance costs over subjective preferences; keep recommendations within scope.
Merge findings that share a root cause, rank them by the priorities in root
`AGENTS.md`, and list open invariant and compatibility questions separately.
