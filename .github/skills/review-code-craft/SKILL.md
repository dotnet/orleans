---
name: review-code-craft
description: Code craft lens for Orleans — control flow, function shape, scope, signatures, conditions, naming, units, and compiler strictness. Use during code review, and when writing or refactoring code.
---

# Code craft lens

Apply with root `AGENTS.md` and `.editorconfig`. This lens examines the code as
the next engineer reads it: control flow, function shape, scope, signatures, and
names. It covers changed code; unrelated refactoring stays out of scope.

## Procedure

1. Read the change top-down as a newcomer would.
2. Flag control flow that hides cases, functions that do more than one thing, mutable state with more than one copy, and names that misdescribe.
3. Prefer refactors that remove code or branches to those that add structure.
4. Keep behavior identical when refactoring, verified by existing tests.
5. Skip formatting the build already enforces.

## Checklist

- **Push `if`s up, `for`s down.** Callers decide which case applies and pass helpers the whole batch; the coordinating method owns state mutation while the helpers it calls compute results without side effects ([matklad](https://matklad.github.io/2023/11/15/push-ifs-up-and-fors-down.html)).
- **Functions fit on a screen.** A function reads as one unit; one beyond about 70 lines prompts a check for mixed responsibilities.
- **Smallest scope.** Values are computed close to their use, so nothing can invalidate a check between the check and the use; each piece of mutable state has one owner and one copy.
- **Simple signatures.** Each extra return dimension (nullability, error result, async) adds branches to every caller, so a method returns the simplest shape its contract allows (`void` over `bool` over `int` over `int?`).
- **Positive conditions.** Conditions read in positive form (`index < count`, not `!(index >= count)`); compound conditions whose terms need different handling are split, and each branch is handled deliberately.
- **Explicit options.** Behavior-critical choices are visible at the call site or the configuration boundary that owns them.
- **Precise names.** Names are precise nouns and verbs with one meaning each; abbreviations are limited to established terms (ID, ETag, GC, RPC).
- **Units in types.** Durations use `TimeSpan`, instants use `DateTimeOffset` in new APIs and the established type in existing contracts, and raw numbers carry the unit in the name (`timeoutMs`).
- **Grouped names.** In new code, related names share a prefix so they sort together (`LatencyMax`, `LatencyMin`), unless local conventions establish another pattern.
- **Compiler strictness.** Code builds cleanly under warnings-as-errors and the repository's nullable settings; each null-forgiving operator or suppression relies on an invariant that is evident nearby or stated in a comment.

## Findings

Report findings ordered by severity, each with the location, the principle at
stake, and a concrete fix.
