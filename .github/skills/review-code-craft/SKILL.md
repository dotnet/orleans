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
2. Flag control flow that hides cases, functions that do more than one thing, state with more than one copy, and names that misdescribe.
3. Prefer refactors that remove code or branches to those that add structure.
4. Keep behavior identical when refactoring, verified by existing tests.
5. Skip formatting the build already enforces.

## Checklist

- **Simple, explicit control flow.** [Push `if`s up and `for`s down](https://matklad.github.io/2023/11/15/push-ifs-up-and-fors-down.html); the parent centralizes state mutation and leaves stay pure.
- **Functions fit on a screen** (about 70 lines).
- **Smallest scope.** Values are computed close to their use, with no gap between check and use; each piece of state has one copy and no aliases.
- **Simple signatures.** Each extra return dimension (nullable, error, async) spreads branches to every caller: `void` > `bool` > `int` > `int?`.
- **Positive conditions** (`index < count`); compound conditions are split and both branches handled.
- **Explicit options.** Behavior-critical options are passed at call sites.
- **Precise names.** Nouns and verbs are precise, unabbreviated, and carry one meaning each.
- **Units in types.** Durations and instants use `TimeSpan` and `DateTimeOffset`; raw numbers carry the unit in the name (`timeoutMs`).
- **Grouped names.** Related names share a prefix (`LatencyMax`, `LatencyMin`).
- **Compiler strictness.** Code builds cleanly under the repository's warnings-as-errors and nullable settings; nullable annotations state the contract, and each null-forgiving operator or suppression carries its reason.

## Findings

Report findings ordered by severity, each with the location, the principle at
stake, and a concrete fix.
