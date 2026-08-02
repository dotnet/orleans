---
title: Grain version selector strategies
description: Choose an eligible grain interface version when Orleans places a new activation.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain version selector strategies

After the compatibility strategy filters available interface versions, the selector strategy determines which versions remain eligible for placement.

| Strategy | Eligible versions |
|---|---|
| <xref:Orleans.Versions.Selector.AllCompatibleVersions> | Every compatible version |
| <xref:Orleans.Versions.Selector.LatestVersion> | The highest compatible version |
| <xref:Orleans.Versions.Selector.MinimumVersion> | The lowest compatible version |

## All compatible versions

This is the default. It returns all compatible versions, and Orleans placement selects a compatible silo. Distribution therefore follows the available compatible silos, not a guaranteed equal percentage per version.

With request version 1, available versions 1 and 2, and `BackwardCompatible`, both versions are eligible.

## Latest version

`LatestVersion` returns only the highest compatible version. It moves new activations toward the newest deployment while preserving the compatibility filter.

With request version 1, available versions 1 and 2, and `BackwardCompatible`, only version 2 is eligible. With request version 3, neither version is compatible, so placement can't satisfy the request.

## Minimum version

`MinimumVersion` returns only the lowest compatible version. It can keep older compatible implementations serving older callers during staged validation.

With request version 1 and available versions 2 and 3 under `BackwardCompatible`, version 2 is selected. With request version 3, only version 3 or newer can be compatible; the selector never bypasses the compatibility rule to choose version 2.

## Existing activations

Changing a selector affects placement of new activations. It doesn't proactively replace compatible existing activations. An activation is replaced when it becomes incompatible with a request, is deactivated normally, or its silo leaves the cluster.
