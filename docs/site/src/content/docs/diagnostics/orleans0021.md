---
title: "ORLEANS0021: Duplicate grain interface declaration"
description: Understand and resolve ORLEANS0021 when OrleansContracts.txt declares an interface identity more than once.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0021: Duplicate grain interface declaration

| Property | Value |
| --- | --- |
| Category | Orleans.Versioning |
| Severity | Warning |
| Code fix | Not available |

## Cause

`OrleansContracts.txt` repeats an interface CLR name or a non-empty `GrainInterfaceType`, including active and retired declarations with the same identity.

## Impact

The manifest is ambiguous. The parser retains the first declaration, so compatibility review can use the wrong version or method set.

## How to fix

Merge the declarations into one canonical entry. Keep one active declaration when the interface exists, or one retired declaration when it has been removed.

## Suppress the diagnostic

Repair the malformed manifest instead of suppressing this diagnostic.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0021.severity = none
```
