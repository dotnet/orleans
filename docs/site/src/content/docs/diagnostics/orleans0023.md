---
title: "ORLEANS0023: Grain class identity mismatch"
description: Understand and resolve ORLEANS0023 when a grain class GrainType differs from OrleansContracts.txt.
ms.date: 08/27/2026
ms.topic: reference
---

# ORLEANS0023: Grain class identity mismatch

| Property | Value |
| --- | --- |
| Category | Orleans.Versioning |
| Severity | Warning |
| Code fix | Available |

## Cause

A grain class matches a manifest declaration by CLR name, but its effective source `GrainType` differs from the manifest.

## Impact

This is a runtime identity change. It can create a new logical grain type, alter routing and storage identity, and break mixed-version operation.

## How to fix

Restore the previous `[GrainType]` when the change was accidental. Update the manifest only when the identity break is intentional and its deployment and state migration have been designed.

The **Update grain class alias in OrleansContracts.txt** code fix accepts the source identity as the new baseline. Review the identity change before applying it.

Apply **Regenerate OrleansContracts.txt** to rebuild the complete project manifest, or use **Fix all in solution** to update every affected project. Review the generated diff using the [contract compatibility guidance](../grains/grain-versioning/contract-compatibility-analyzer.md#regenerate-the-manifest).

## Suppress the diagnostic

Suppress only for a deliberate, documented identity migration. Updating the reviewed baseline is preferable to retaining a suppression.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0023.severity = none
```
