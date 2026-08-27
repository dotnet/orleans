---
title: "ORLEANS0024: Removed grain class is not retired"
description: Understand and resolve ORLEANS0024 when OrleansContracts.txt contains an active grain class that source no longer defines.
ms.date: 08/27/2026
ms.topic: reference
---

# ORLEANS0024: Removed grain class is not retired

| Property | Value |
| --- | --- |
| Category | Orleans.Versioning |
| Severity | Warning |
| Code fix | Available |

## Cause

An active class declaration in `OrleansContracts.txt` does not match any compiled concrete grain class.

## Impact

The removal or identity-changing rename is not recorded, and the old grain identity can later be reused accidentally.

## How to fix

Restore the class if its removal was accidental. Otherwise apply **Mark grain class as *RETIRED* in OrleansContracts.txt** and preserve the declaration.

Apply **Regenerate OrleansContracts.txt** to rebuild the complete project manifest and retire every declaration absent from source, or use **Fix all in solution** to update every affected project. Review the generated diff using the [contract compatibility guidance](../grains/grain-versioning/contract-compatibility-analyzer.md#regenerate-the-manifest).

## Suppress the diagnostic

Suppress only when the manifest intentionally includes classes owned by another compilation. Prefer separate project manifests.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0024.severity = none
```
