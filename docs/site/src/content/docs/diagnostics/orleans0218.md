---
title: "ORLEANS0218: Pass constant version names and ranges"
description: Understand ORLEANS0218, which reports version checks whose change name or supported range is computed at run time, and fix it with constants or a DurableVersion descriptor.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0218: Pass constant version names and ranges

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

`DurableTask.GetVersion` or the `DurableVersion` constructor receives a name, `minSupported`, or `maxSupported` argument which is computed at run time, such as an interpolated string or a parameter:

:::code source="snippets/durable-tasks/Orleans0218.cs" id="orleans0218_violation" language="csharp":::

## Rule description

The first check of a change in an execution records the version under the change's name, and every later check, including every replay, reads the recorded version back under the same name. The execution's branches follow that version, so each replay schedules the operations which the first run scheduled.

A name built from data names one change per value. When the data differs between a run and its replay, or a deployment builds the name differently, the replay checks a name which the execution never recorded, records a second decision, and can take another branch than the run which scheduled the recorded operations. The replay then diverges.

The range states which versions the deployed code supports: `maxSupported` is the newest version which the code implements, and `minSupported` is the oldest version which it still replays. Both belong to the code which branches on the version. A range which callers pass can admit a version for which the code has no branch, or retire a version which pending executions recorded, which stalls them.

For versioning, see [Version durable tasks](../grains/durable-tasks-versioning.md).

## How to fix violations

**Pass constants.** Name the change with a constant, and state the range which the branches implement. Data which selects behavior, such as the region, goes to the operations in each branch:

:::code source="snippets/durable-tasks/Orleans0218.cs" id="orleans0218_fix_constant" language="csharp":::

**Declare a `DurableVersion`.** A static descriptor states the change's name and range once, and the body reads it with `Get` or `IsAtLeast`:

:::code source="snippets/durable-tasks/Orleans0218.cs" id="orleans0218_fix_descriptor" language="csharp":::

## When to suppress

Suppress the diagnostic for a value which stays constant for the lifetime of every execution which checks it, such as a name which a generated constant class supplies through a `static readonly` field.
