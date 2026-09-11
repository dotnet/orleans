---
title: Cluster manifest retrieval
description: How Orleans discovers silo manifests, reuses content hashes, and repairs missing metadata during membership changes.
ms.date: 09/10/2026
ms.topic: concept-article
---

# Cluster manifest retrieval

Each silo publishes an immutable grain manifest describing its grain types, interfaces, and properties. The cluster manifest provider assembles these manifests into the metadata used for type resolution and version-aware placement.

## Membership and publication

Cluster manifests have a major version corresponding to the membership version and a minor version which advances as manifests arrive. When membership advances, the provider synchronously prunes non-active silos and includes the local silo when it is active. Local grain metadata also remains available while the local silo is starting.

The default retrieval path requests each missing active silo's manifest directly. Successful responses are published together after the outstanding fetches complete; unsuccessful fetches are retried after five seconds or when a newer membership snapshot arrives. Cancellation reaches the remote call and stops local waiting.

## Content-addressed retrieval

<xref:Orleans.Configuration.ClusterManifestOptions.EnableContentAddressedRetrieval> enables hash-based retrieval. Its default is `false`. Configure <xref:Orleans.Configuration.ClusterManifestOptions> through the silo builder's options configuration before starting the silo. The provider captures the value at construction; a restart applies configuration changes.

With this option enabled, the provider asks each missing silo for the content hash of its local manifest. A matching entry in the local hash cache supplies the manifest immediately. Otherwise, the provider requests the manifest by hash, verifies its content, and adds it to the cache.

Hashes use SHA-256 over a versioned canonical frame. The frame sorts grain and interface identifiers and property keys, includes collection counts and structural boundaries, and preserves raw identifier bytes, null and empty values, and UTF-16 code units. Equivalent content therefore shares a hash regardless of dictionary insertion order. Hashes of immutable manifest instances are memoized with weak keys, allowing the manifests to be collected when their owners release them.

Each publication creates a fresh cache containing its live manifests and local metadata. In-flight retrievals retain their original cache instance. A stale retrieval can populate that instance, while newer publications retain their own cache. The provider exposes the new manifest version before its new cache. Retrievals capture the cache before checking the version, so an older update which captures the newer cache also observes that its membership snapshot has been superseded.

## Peer repair and bounded waiting

When more than one active silo's manifest is missing, the enabled provider also probes up to three peers selected from a rotating ordered membership list. Direct retrieval starts alongside these probes.

Each probe has a one-second deadline shared by its hash-summary and manifest-update requests. Cached hashes can satisfy missing entries directly; otherwise the provider requests a complete update from the peer and verifies each candidate against the summary's hash. Successfully repaired entries are published immediately, including partial repairs. Direct requests for those entries are removed from the required completion set, allowing the remaining successful fetches to advance the manifest independently.

The provider admits at most three concurrent local probe attempts. Completion, timeout, and caller cancellation release the attempt's slot immediately, allowing later retries to proceed. The one-second deadline cancels the request token and ends local waiting; Orleans signals cancellation to the peer using the ordinary RPC path. Direct retrieval continues alongside these attempts. Late responses leave the completed attempt's result unchanged, and late failures are observed and logged. Retry selection rotates through active peers, with retries paced by the five-second delay or a newer membership snapshot.

## Compatibility, rollout, and rollback

Silos serve hash requests on demand, including when they use the default retrieval path themselves. Construction and direct retrieval perform their ordinary metadata work; hash computation begins when enabled retrieval or an incoming hash request needs it.

During a rolling deployment, enabled silos try hash retrieval and fall back to the established direct RPC when a peer rejects the newer method or its hash request fails. An invalid or unavailable hash-addressed body also triggers direct retrieval. Independent remote cancellation follows that compatibility path; cancellation of the local request propagates to the caller. Peer-repair failures leave direct retrieval responsible for filling the missing entries.

Enable the option on a small set of silos first and observe manifest retrieval and peer-probe diagnostics during joins and restarts. Debug logs distinguish hash fallback, peer timeout, occupied probe slots, and late failures. Warnings identify failed direct fetches. For rollback, set the option to `false` and restart the affected silos; they resume direct retrieval while continuing to answer enabled peers' hash requests on demand.

See [cluster membership](cluster-management.md) for membership transitions and [rolling version skew](rolling-version-skew.md) for how manifest metadata drives interface-version selection.
