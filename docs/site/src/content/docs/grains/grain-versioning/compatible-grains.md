---
title: Compatible grains
description: Learn about compatible grains in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Compatible grains

When an existing grain activation is about to process a request, the runtime checks if the version in the request and the actual version of the grain are compatible. Orleans doesn't infer at runtime which policy to use. The default behavior for determining compatibility between two versions is defined by <xref:Orleans.Versions.Compatibility.CompatibilityStrategy?displayProperty=nameWithType>.

## Backward compatible (default)

### Definition

A grain interface version `Vn` can be backward compatible with `Vm` if:

- The name of the interface didn't change (or the overridden type code).
- All public methods present in the `Vm` version are also present in the `Vn` version.
    **It's important not to modify the signatures of methods inherited from `Vm`**: since Orleans uses an internal built-in serializer, modifying or renaming a field (even private) can break serialization.

Since `Vn` can have added methods compared to `Vm`, `Vm` is not compatible with `Vn`.

### Example

If you have two versions of a given interface in the cluster, V1 and V2, and V2 is backward compatible with V1:

- If the current activation is V2 and the requested version is V1, the current activation can process the request normally.
- If the current activation is V1 and the requested version is V2, Orleans deactivates the current activation and creates a new activation compatible with V2 (see [Version selector strategy](version-selector-strategy.md)).

## Fully compatible

### Definition

A grain interface version `Vn` can be fully compatible with `Vm` if:

- `Vn` is backward compatible with `Vm`.
- No public methods were added in the `Vn` version.

If `Vn` is fully compatible with `Vm`, then `Vm` is also fully compatible with `Vn`.

### Example

If you have two versions of a given interface in the cluster, V1 and V2, and V2 is fully compatible with V1:

- If the current activation is V2 and the requested version is V1, the current activation can process the request normally.
- If the current activation is V1 and the requested version is V2, the current activation can also process the request normally.
