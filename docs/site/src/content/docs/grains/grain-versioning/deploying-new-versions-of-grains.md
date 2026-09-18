---
title: Deploy new grain interface versions
description: Roll out a new Orleans grain interface version while preserving caller compatibility.
ms.date: 08/23/2026
ms.topic: how-to
---

# Deploy new grain interface versions

A safe rolling upgrade combines version routing with an application contract that newer implementations can honor for older callers.

## Prepare the contract

Before deployment:

1. Keep existing method identities, parameter meanings, return meanings, and serialized payloads compatible.
1. Add new behavior using new methods or optional/version-tolerant payload fields.
1. Make the new implementation able to process every request sent by still-deployed callers.
1. Make persisted state readable by both versions if rollback is required.
1. Test mixed caller and silo versions, not only each version in isolation.

Apply a higher `[Version]` value only after defining this contract. The number asserts routing compatibility; it doesn't create compatibility.

When a rollout adds grain interface methods, first ensure every client-to-gateway and silo-to-silo path runs an Orleans runtime which supports independent message-body type references. Those connections preserve a request whose generated invocation type is unavailable on an application-version-old silo and return it to version-aware placement. The compatible destination still needs to deserialize the request payload, and the caller still needs to deserialize the response.

## Rolling upgrade

The default combination works for a backward-compatible rollout:

:::code language="csharp" source="../../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="configure_grain_versioning":::
Then:

1. Start version 2 silos while version 1 silos and callers remain.
1. Version 1 requests can use version 1 or version 2 activations.
1. Start version 2 callers only after enough version 2 silos are ready.
1. When a version 2 request reaches a version 1 activation, Orleans preserves the logical request, deactivates the incompatible activation, and places a compatible version.
1. Drain version 1 callers, then version 1 silos.
1. Keep the backward-compatible contract until rollback is no longer required.

Use <xref:Orleans.Versions.Selector.LatestVersion> when new activations should prefer the newest compatible implementation. Use <xref:Orleans.Versions.Selector.MinimumVersion> when new activations should stay on the lowest compatible version during staged validation. Neither strategy upgrades compatible activations proactively.

## Rollback

Routing rollback is only safe if the older code can read state and messages written by the newer code. If version 2 writes an incompatible storage representation or emits incompatible payloads, stopping version 2 silos doesn't restore compatibility.

Plan data evolution and interface evolution together:

- Deploy backward-compatible readers first.
- Delay irreversible writes until rollback is no longer needed.
- Use operation and schema version fields where semantics can diverge.
- Exercise rollback in a mixed-version test cluster.

## Observe the rollout

Monitor incompatible-request deactivations, activation placement by version, failed placements, serialization failures, and state-read failures. A rising rate of replacement activations can indicate incompatible callers sharing hot grain identities or an overly strict strategy.
