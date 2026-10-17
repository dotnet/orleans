---
title: "ORLEANS0118: Pass constant version names and ranges"
description: Understand ORLEANS0118, which reports a DurableTask.GetVersion call whose change name or supported range isn't a constant, and fix it with constants.
ms.date: 10/16/2026
ms.topic: reference
---

# ORLEANS0118: Pass constant version names and ranges

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A call to `DurableTask.GetVersion` passes a change name, a minimum supported version, or a maximum supported version which isn't a compile-time constant:

:::code source="snippets/durable-tasks/Orleans0118.cs" id="orleans0118_violation" language="csharp":::

## Rule description

An execution records the version of each change which it checks under the change's name, and every replay of the execution reads the version back under the same name. A name built from data, such as a region or a counter, records a separate decision for each value, so checks which concern one change can follow different versions in one execution.

The supported range states which branches the calling code keeps: `maxSupported` is the newest branch, and `minSupported` the oldest. A range which comes from arguments or configuration changes independently of the branches, so new executions can record a version which no branch handles, and executions which a branch still handles can stall.

For versioning, see [Version durable tasks](../grains/durable-tasks-versioning.md).

## How to fix violations

Name the change with a string literal or a constant, and pass the range as constants next to the branches which they describe:

:::code source="snippets/durable-tasks/Orleans0118.cs" id="orleans0118_fix" language="csharp":::

## When to suppress

Suppress the diagnostic for a helper which forwards a constant name and range from its callers, such as a method which wraps `GetVersion` for one change, and whose callers pass constants.
