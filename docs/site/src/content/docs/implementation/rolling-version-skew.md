---
title: Rolling version skew
description: Explain how Orleans routes grain interface versions and preserves compatibility while silos run different builds.
ms.date: 08/23/2026
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

The selector converts the compatibility decision into a placement set; applications preserve contract compatibility across that set.

## Activation checks

Placement chooses a suitable silo, and an activation validates the incoming interface version against its local implementation. An incompatible activation invalidates the stale route and returns the existing message to routing. Version changes can therefore move activation or repeat routing while preserving the logical call.

## Wire compatibility is a separate contract

Connections using <xref:Orleans.Runtime.Messaging.NetworkProtocolVersion.Version2> encode each message body with independent type references. When an application-version-old silo lacks a generated invocation type for a request, it preserves the original body bytes and returns the same logical message to version-aware routing. A compatible destination then decodes and invokes the request.

Connections negotiate the highest protocol version supported by both endpoints. Stage Orleans runtime upgrades before deploying application contracts which depend on forwarding unknown invocation types, so every relevant connection negotiates version 2.

The destination must deserialize the request payload, and the caller must deserialize the response. Keep serialization IDs stable, add fields with new IDs, retain aliases when CLR names move, and ensure custom codecs skip unknown fields. These rules also apply to mixed traffic, queued messages, reminders, streams, and persisted state. See [serialization and code generation internals](serialization.md) for the wire-level rules.

Grain-state compatibility remains an application schema responsibility. Both implementations read the stored representation and tolerate writes from the other while rollback or mixed placement remains possible.

## Failure modes and rollout implications

- An empty compatible-silo set causes placement to reject or time out the request.
- A stale activation triggers cache invalidation and routing repair for the same logical message; the runtime preserves its message identity while locating a compatible activation.
- A connection which negotiates protocol version 1 surfaces an unknown invocation decode or forwarding failure to the caller.
- Changing a selector or compatibility strategy resets the suitable-silo cache, so subsequent placements reflect the new policy.
- Removing the old implementation before all callers and durable data are compatible can turn a planned rolling deployment into an outage.

The [interface versioning guide](../grains/grain-versioning/grain-versioning.md) and [deployment and rollback guidance](../migration/deployment-and-rollback.md) cover configuration and rollout procedures. This runtime decision chain connects interface manifests, compatibility selection, placement, activation validation, and wire compatibility.

Source: [`GrainVersionManifest`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/Manifest/GrainVersionManifest.cs), [`CachedVersionSelectorManager`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Versions/CachedVersionSelectorManager.cs), [`AllCompatibleVersionsSelector`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Versions/Selector/AllCompatibleVersionsSelector.cs), and [`ActivationData`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Catalog/ActivationData.cs).
