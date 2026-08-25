---
title: "ORLEANS0014: Preserve the grain execution context"
description: Understand and resolve ORLEANS0014 when ConfigureAwait can move a continuation outside the grain execution context.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0014: Preserve the grain execution context

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Warning |
| Code fix | Available |

## Cause

A grain or system-target method uses `ConfigureAwait(false)`, or uses `ConfigureAwaitOptions` without `ContinueOnCapturedContext`.

## Impact

The continuation can execute outside the grain scheduler and lose the activation's execution context. Code after the await can then violate Orleans' turn-based concurrency guarantees or observe the wrong grain context.

## How to fix

Prefer an ordinary `await`. When `ConfigureAwait` is required, use `ConfigureAwait(true)` or include `ConfigureAwaitOptions.ContinueOnCapturedContext`.

The code fix changes `false` to `true`, replaces `ConfigureAwaitOptions.None`, or adds `ContinueOnCapturedContext` to the existing options.

## Suppress the diagnostic

Suppress this diagnostic only when the complete continuation is intentionally context-free and cannot access grain or runtime state. Review the full post-await call path before suppressing it.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0014.severity = none
```
