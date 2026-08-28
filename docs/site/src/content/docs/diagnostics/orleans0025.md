---
title: "ORLEANS0025: Duplicate grain class declaration"
description: Understand and resolve ORLEANS0025 when OrleansContracts.txt declares a grain identity more than once.
ms.date: 08/27/2026
ms.topic: reference
---

# ORLEANS0025: Duplicate grain class declaration

| Property | Value |
| --- | --- |
| Category | Versioning |
| Severity | Warning |
| Code fix | Not available |

## Cause

`OrleansContracts.txt` repeats an effective grain class identity. The effective identity is `GrainType` when present and the identity derived from the recorded CLR name using Orleans conventions for a legacy declaration.

## Impact

The grain identity history becomes ambiguous, and the parser accepts only the first declaration.

## How to fix

Merge declarations which have the same effective identity into one canonical entry. Active and retired declarations can share a CLR name when they record different explicit `GrainType` values across an identity migration.

## Suppress the diagnostic

Repair the malformed manifest instead of suppressing this diagnostic.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0025.severity = none
```
