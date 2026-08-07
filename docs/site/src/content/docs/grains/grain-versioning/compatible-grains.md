---
title: Grain interface compatibility strategies
description: Configure numeric compatibility rules for Orleans grain interface versions.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain interface compatibility strategies

A compatibility strategy answers one question:

```text
Can an activation at currentVersion process a request at requestedVersion?
```

The built-in strategies compare only those numeric values.

| Strategy | Numeric rule | Default |
|---|---|---|
| <xref:Orleans.Versions.Compatibility.BackwardCompatible> | `requestedVersion <= currentVersion` | Yes |
| <xref:Orleans.Versions.Compatibility.AllVersionsCompatible> | Always compatible | No |
| <xref:Orleans.Versions.Compatibility.StrictVersionCompatible> | `requestedVersion == currentVersion` | No |

## Backward compatible

With <xref:Orleans.Versions.Compatibility.BackwardCompatible>, a version 2 activation can process a version 1 request, but a version 1 activation can't process a version 2 request.

Use it when each newer implementation preserves every contract needed by older callers. Newer callers can use additions that require a newer activation, while older callers can run on either version.

## All versions compatible

<xref:Orleans.Versions.Compatibility.AllVersionsCompatible> allows any request version to use any activation version. Orleans still performs no structural check.

Use this only when deployed contracts are genuinely compatible in both directions, such as an implementation-only change with an intentionally incremented routing version. If a newer caller uses a method or payload unsupported by an older activation, this strategy can route the call to an implementation that can't honor it.

## Strict version compatible

<xref:Orleans.Versions.Compatibility.StrictVersionCompatible> requires an exact numeric match. It isolates versions but reduces placement choices and causes an incompatible existing activation to be deactivated when another version addresses the same grain identity.

Use it when versions must not share activations. It is usually a poor default for a gradual rolling upgrade because callers of different versions can repeatedly replace one another's activations.

## "Fully compatible" is a contract, not a strategy

Fully compatible describes an application contract that works in both directions. It isn't the name of a built-in strategy. If the contract is truly bidirectional, <xref:Orleans.Versions.Compatibility.AllVersionsCompatible> represents that assertion to the runtime.
