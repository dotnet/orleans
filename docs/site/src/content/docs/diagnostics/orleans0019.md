---
title: "ORLEANS0019: Removed grain interface is not retired"
description: Understand and resolve ORLEANS0019 when OrleansContracts.txt contains an active interface that source no longer defines.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0019: Removed grain interface is not retired

| Property | Value |
| --- | --- |
| Category | Orleans.Versioning |
| Severity | Warning |
| Code fix | Available |

## Cause

An active interface declaration in `OrleansContracts.txt` does not match any compiled grain interface.

## Impact

The deletion or identity-changing rename is not recorded as intentional, and the old interface identity can later be reused accidentally.

## How to fix

Restore the interface if its removal was accidental. Otherwise apply **Mark as *RETIRED* in OrleansContracts.txt**. Preserve retired declarations as contract history.

## Suppress the diagnostic

Suppression is appropriate only when the manifest intentionally contains contracts owned by another compilation. Prefer one manifest per project so ownership remains explicit.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0019.severity = none
```
