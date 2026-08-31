---
title: "ORLEANS0022: Grain class is not active in OrleansContracts.txt"
description: Understand and resolve ORLEANS0022 when a concrete grain class is missing or retired in the contract manifest.
ms.date: 08/27/2026
ms.topic: reference
---

# ORLEANS0022: Grain class is not active in OrleansContracts.txt

| Property | Value |
| --- | --- |
| Category | Versioning |
| Severity | Warning |
| Code fix | Available |

## Cause

A concrete grain or system-target class has no active manifest declaration matching its effective `GrainType`.

## Impact

The implementation identity is not protected by contract review. A CLR rename without a stable `[GrainType]`, or a `GrainType` change, can create a distinct logical grain type and disrupt routing or state continuity.

## How to fix

Verify the class's durable grain type, add `[GrainType]` when it must remain independent of the CLR name, and apply **Add to OrleansContracts.txt**. The code fix adds or reactivates the class declaration.

Apply **Regenerate OrleansContracts.txt** to rebuild the complete project manifest. Run regeneration separately for each contract project and review the generated diff using the [contract compatibility guidance](../grains/grain-versioning/contract-compatibility-analyzer.md#regenerate-the-manifest).

## Suppress the diagnostic

Suppress only for a grain class intentionally excluded from deployment-contract tracking.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0022.severity = none
```
