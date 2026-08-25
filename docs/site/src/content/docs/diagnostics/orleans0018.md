---
title: "ORLEANS0018: Grain interface member not declared"
description: Understand and resolve ORLEANS0018 when an RPC method signature is missing from OrleansContracts.txt.
ms.date: 08/25/2026
ms.topic: reference
---

# ORLEANS0018: Grain interface member not declared

| Property | Value |
| --- | --- |
| Category | Orleans.Versioning |
| Severity | Warning |
| Code fix | Available |

## Cause

An ordinary grain-interface method has no matching contract signature in `OrleansContracts.txt`. Method identity, generic arity, parameter types and order, and return type are part of the signature. Parameter names are not.

## Impact

Older activations can receive an unknown RPC, and changed identities or payload types can cause dispatch or serialization failures during a rolling upgrade.

## How to fix

Prefer preserving the existing method and adding a new method for changed behavior. Review payload compatibility, increment the interface version when appropriate, and apply **Add to OrleansContracts.txt**. The code fix records the new signature but does not increment `[Version]`.

## Suppress the diagnostic

Suppress only for a demonstrated signature-normalization false positive where the source and manifest represent the same contract.

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0018.severity = none
```

See [Backward compatibility guidelines](../grains/grain-versioning/backward-compatibility-guidelines.md).
