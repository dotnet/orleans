---
title: Rolling version skew
description: Explain how Orleans routes grain interface versions and preserves compatibility while silos run different builds.
ms.date: 08/11/2026
ms.topic: concept-article
---

# Rolling version skew

During a rolling deployment, silos can advertise different versions of the same grain interface. Orleans separates two questions: **compatibility** asks whether a requested version can be served by a candidate version, and **selection** chooses which compatible candidate to use. The grain version manifest, compatibility directors, selectors, and placement service answer those questions together.

## Version discovery and selection

Each silo contributes supported interface versions to `GrainVersionManifest`. The cached selector manager keys its result by grain type, interface, and requested version. It obtains available versions, filters them through the configured compatibility director, selects one or more versions, and maps those versions to suitable silos. The cache is invalidated when version or compatibility strategies change.

The built-in strategies have intentionally different upgrade behavior:

| Strategy | Effect |
| --- | --- |
| Strict compatibility | Only the same interface version is eligible. |
| Backward compatibility | A newer implementation may serve an older request when the director says it is compatible. |
| All versions compatible | Every advertised version is eligible. |
| Latest selector | Choose the highest compatible version. |
| Minimum selector | Choose the lowest compatible version. |
| All compatible selector | Keep all compatible versions as placement candidates. |

The selector does not make an incompatible contract safe. It only turns the compatibility decision into a placement set.

## Activation checks

Placement chooses a suitable silo, but an activation also validates the incoming interface version against its local implementation. If the activation's version is not compatible, the runtime invalidates the stale route and returns the message to routing rather than executing the method against an incompatible implementation. This is why a version change can cause activation movement or a retry of routing without being an application-level call retry.

## Wire compatibility is a separate contract

Version routing cannot repair incompatible bytes. Request and response types must remain readable by both old and new builds for as long as mixed traffic, queued messages, reminders, streams, or persisted state can cross the boundary. Keep serialization IDs stable, add fields instead of reusing IDs, retain aliases when CLR names move, and ensure custom codecs skip fields they do not understand. See [serialization and code generation internals](serialization.md) for the wire-level rules.

Interface versioning also does not version grain state. Both implementations must read the stored representation and tolerate writes from the other while rollback or mixed placement remains possible.

## Failure modes and rollout implications

- If no compatible silo is available, placement cannot complete the request and the caller receives a rejection or eventually a timeout.
- If a stale activation receives a request, cache invalidation and routing repair can locate a compatible activation; this is not an automatic duplicate invocation policy.
- Changing a selector or compatibility strategy resets the suitable-silo cache, so subsequent placements reflect the new policy.
- Removing the old implementation before all callers and durable data are compatible can turn a planned rolling deployment into an outage.

The [interface versioning guide](../grains/grain-versioning/grain-versioning.md) and [deployment and rollback guidance](../migration/deployment-and-rollback.md) own configuration and rollout procedures. This page documents the runtime decision chain.

Source: [`GrainVersionManifest`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/Manifest/GrainVersionManifest.cs), [`CachedVersionSelectorManager`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Versions/CachedVersionSelectorManager.cs), [`AllCompatibleVersionsSelector`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Versions/Selector/AllCompatibleVersionsSelector.cs), and [`ActivationData`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Catalog/ActivationData.cs).
