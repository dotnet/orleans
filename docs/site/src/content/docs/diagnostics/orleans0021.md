---
title: "ORLEANS0021: Duplicate grain interface declaration"
description: Understand and resolve ORLEANS0021 when OrleansContracts.txt declares an interface identity more than once.
ms.date: 08/27/2026
ms.topic: reference
---

# ORLEANS0021: Duplicate grain interface declaration

| Property | Value |
| --- | --- |
| Category | Versioning |
| Severity | Warning |
| Code fix | Not available |

## Cause

`OrleansContracts.txt` repeats an effective interface identity. The effective identity is `GrainInterfaceType` when present and the identity derived from the recorded CLR name using Orleans conventions for a legacy declaration.

## Impact

The manifest is ambiguous. The parser retains the first declaration, so compatibility review can use the wrong version or method set.

## How to fix

Merge declarations which have the same effective identity into one canonical entry. Active and retired declarations can share a CLR name when they record different explicit `GrainInterfaceType` values across an identity migration.

## Suppress the diagnostic

Repair the malformed manifest instead of suppressing this diagnostic.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0021.severity = none
```
