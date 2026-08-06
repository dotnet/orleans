---
title: Version selector strategy
description: Learn how to use the version selector strategy in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Version selector strategy

When several versions of the same grain interface exist in the cluster and a new activation needs to be created, Orleans chooses a [compatible version](compatible-grains.md) according to the strategy defined in <xref:Orleans.Configuration.GrainVersioningOptions.DefaultVersionSelectorStrategy?displayProperty=nameWithType>.

Orleans supports the following strategies out of the box:

## All compatible versions (default)

Using this strategy, Orleans chooses the version of the new activation randomly from all compatible versions.

For example, if you have two versions of a given grain interface, V1 and V2:

- V2 is backward compatible with V1.
- In the cluster, 2 silos support V2, and 8 support V1.
- The request was made from a V1 client/silo.

In this case, there's a 20% chance the new activation will be V2 and an 80% chance it will be V1.

## Latest version

Using this strategy, the version of the new activation is always the latest compatible version. For example, if you have two versions of a given grain interface, V1 and V2 (where V2 is backward or fully compatible with V1), then all new activations will be V2.

## Minimum version

Using this strategy, the version of the new activation is always the requested version or the minimum compatible version. For example, if you have two versions of a given grain interface, V2 and V3, both fully compatible:

- If the request was made from a V1 client/silo, the new activation will be V2.
- If the request was made from a V3 client/silo, the new activation will also be V2.
