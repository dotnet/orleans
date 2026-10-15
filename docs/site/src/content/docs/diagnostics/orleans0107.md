---
title: "ORLEANS0107: Use durable operation names without '$'"
description: Understand ORLEANS0107, which reports a WithId or DurableTask.Once name which contains '$', and fix it by choosing another separator.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0107: Use durable operation names without '$'

| Property | Value |
| --- | --- |
| Severity | Error |
| Category | Usage |
| Code fix | None |

## Cause

A constant name which `WithId` or `DurableTask.Once` receives inside a durable task body contains `$`:

:::code source="snippets/durable-tasks/Orleans0107.cs" id="orleans0107_violation" language="csharp":::

## Rule description

The runtime generates identifiers which contain `$`: unnamed children and effects, such as `$child-1`; effect records, such as `$once:key`; repeated names, such as `charge$2`; and runtime records, such as `$signal:approved`. A name with `$` could equal one of them, so `WithId` and `DurableTask.Once` reject it inside a durable execution with `ArgumentException`, and the task fails when it reaches the operation.

## How to fix violations

Choose a name without `$`, such as one which separates its parts with `-`:

:::code source="snippets/durable-tasks/Orleans0107.cs" id="orleans0107_fix" language="csharp":::

Name a call site once, even when one run repeats it. The runtime numbers repeated occurrences itself, so a loop which awaits `.WithId("charge")` creates `charge`, `charge$2`, and so on.

## When to suppress

Don't suppress this diagnostic: the runtime rejects the name.
