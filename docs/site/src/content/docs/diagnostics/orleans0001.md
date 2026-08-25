---
title: "ORLEANS0001: Place AlwaysInterleave on the grain interface"
description: Understand and resolve ORLEANS0001 when AlwaysInterleave is applied to an implementation method.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0001: Place AlwaysInterleave on the grain interface

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Error |
| Code fix | Not available |

## Cause

`[AlwaysInterleave]` is applied to a method declared by a class. The attribute defines scheduling behavior on the grain contract and belongs on the corresponding grain-interface method.

## Impact

An implementation annotation does not define the remote contract's scheduling behavior. Calls might not interleave as expected, which can create liveness or concurrency problems.

## How to fix

Remove `[AlwaysInterleave]` from the implementation method and apply it to the matching method on the grain interface.

## Suppress the diagnostic

Suppress only for a verified analyzer false positive on a method which is not part of a grain implementation. Removing the ineffective attribute is preferable.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0001.severity = none
```
