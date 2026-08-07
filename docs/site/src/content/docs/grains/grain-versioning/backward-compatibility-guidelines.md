---
title: Grain contract compatibility guidelines
description: Evolve Orleans grain interfaces safely across rolling deployments.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain contract compatibility guidelines

Orleans version routing doesn't validate contracts. A newer activation is backward compatible only when it can correctly process requests produced by every older caller that may reach it.

## Preserve existing methods

Don't remove or change the signature of a method while callers using it remain deployed. This includes parameter order, parameter and return types, generic shape, and method semantics.

Add a new method instead of repurposing an existing one:

```csharp
[Version(2)]
public interface IInventoryGrain : IGrainWithStringKey
{
    // Retained for version 1 callers.
    Task<int> ReserveAsync(string sku, int quantity);

    // Added for version 2 callers.
    Task<ReservationResult> ReserveWithIdAsync(
        string operationId,
        string sku,
        int quantity);
}
```

Mark the old method `[Obsolete]` to stop new usage, but keep it until telemetry and deployment state show no older callers remain.

## Preserve payload contracts

Grain method arguments and return values are serialized contracts. Follow Orleans serializer version-tolerance rules:

- Keep existing `[Id]` values stable.
- Add new fields with new IDs and safe defaults.
- Don't reuse an ID for a different meaning.
- Don't rename or reinterpret values when older code can still observe them.
- Keep exception and result types available to all caller versions.

Parameter names aren't part of dispatch, but changing their semantic order while retaining the same types can silently produce incorrect results.

## Preserve behavior

Signature compatibility isn't enough. A newer implementation serving older callers must preserve invariants, authorization behavior, idempotency, and result meaning expected by those callers.

When behavior must change incompatibly, add a new method or use strict version compatibility and accept the operational cost of version isolation.

## Coordinate with persisted state

Grain interface versioning doesn't version storage. During a rolling upgrade, either implementation can activate for a grain identity allowed by the routing policy. Both versions must read the persisted representation and tolerate writes from the other for as long as rollback or mixed placement is possible.

Use staged schema changes: compatible readers, then new writers, then cleanup after the old version is gone.
