---
title: "ORLEANS0025: Duplicate grain class declaration"
description: Understand and resolve ORLEANS0025 when OrleansContracts.txt declares a grain identity more than once.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0025: Duplicate grain class declaration

| Property | Value |
| --- | --- |
| Category | Orleans.Versioning |
| Severity | Warning |
| Code fix | Not available |

## Cause

`OrleansContracts.txt` repeats a grain class CLR name or a non-empty `GrainType`, including active and retired declarations with the same identity.

## Impact

The grain identity history becomes ambiguous, and the parser accepts only the first declaration.

## How to fix

Merge or remove duplicates, retaining one canonical active or retired declaration.

## Suppress the diagnostic

Repair the malformed manifest instead of suppressing this diagnostic.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0025.severity = none
```
