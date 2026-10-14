---
title: "ORLEANS0028: Suppress CS1998 for async durable task methods"
description: Understand ORLEANS0028, which suppresses CS1998 for async methods which return DurableTask or DurableTask<TResult> without awaiting.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0028: Suppress CS1998 for async durable task methods

| Property | Value |
| --- | --- |
| Kind | Diagnostic suppressor |
| Suppressed diagnostic | CS1998 |
| Code fix | Not applicable |

## Cause

An `async` method, local function, lambda, or anonymous method returns `Orleans.DurableTasks.DurableTask` or `Orleans.DurableTasks.DurableTask<TResult>` and contains no `await` expression. Compilers which report CS1998 warn that such a method runs synchronously, and the durable task analyzers suppress that warning.

## Behavior

The durable task method builder defers the method body. The durable task host runs the body as a durable step under replay control and records its result, so a body which completes without awaiting is a normal step, for example a state transition whose result the parent workflow records. For the execution model, see [Durable tasks](../grains/durable-tasks.md#local-steps-and-calls-to-other-grains).

The `Microsoft.Orleans.DurableTasks.Abstractions` package carries the suppressor with the other durable task analyzers, so it applies to every project which references the package. The suppressor matches the return type by its fully qualified metadata name. CS1998 remains reported for other task-like return types, including `Task`, `ValueTask`, and types named `DurableTask` in other namespaces.

C# compilers from Roslyn 5.0, which the .NET 10 SDK includes, don't report CS1998. The suppression takes effect with earlier compilers.

## Disable the suppression

To report CS1998 for async durable task methods, disable the suppressor by its ID:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);ORLEANS0028</NoWarn>
</PropertyGroup>
```
