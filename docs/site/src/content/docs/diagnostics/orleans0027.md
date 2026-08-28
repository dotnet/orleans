---
title: "ORLEANS0027: Grain interface member removed from source"
description: Understand and resolve ORLEANS0027 when OrleansContracts.txt retains an RPC method which is absent from source.
ms.date: 08/28/2026
ms.topic: reference
---

# ORLEANS0027: Grain interface member removed from source

| Property | Value |
| --- | --- |
| Category | Versioning |
| Severity | Warning |
| Code fix | Not available |

## Cause

`OrleansContracts.txt` declares an RPC method signature which is absent from the matching source grain interface. The value before the colon is the effective method identity Orleans uses on the wire.

## Impact

Removing an RPC method can break calls from older clients or activations during a rolling upgrade. Regeneration retains the historical signature so the wire-contract removal remains visible and requires an explicit decision.

## How to fix

Restore the source method when the removal was accidental. When the removal is intentional, review the mixed-version deployment impact, increment the interface version when appropriate, and explicitly remove the retained signature from `OrleansContracts.txt`.

See [Orleans contract compatibility analyzer](../grains/grain-versioning/contract-compatibility-analyzer.md#regenerate-the-manifest).

## Suppress the diagnostic

Prefer updating the reviewed manifest after accepting the contract removal. Suppress only for a manifest intentionally shared with another compilation.

```ini
[*]
dotnet_diagnostic.ORLEANS0027.severity = none
```
