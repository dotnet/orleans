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

The provider retrieves missing active silos' manifests and publishes successful results. Unsuccessful fetches are retried after five seconds or when a newer membership snapshot arrives. Cancellation reaches the remote call and stops local waiting.

## Content-addressed retrieval

<xref:Orleans.Configuration.ClusterManifestOptions.EnableContentAddressedRetrieval> defaults to `true`, enabling hash-based retrieval and peer repair. Set it to `false` in <xref:Orleans.Configuration.ClusterManifestOptions> to request each missing active silo's manifest directly. Configure the option through the silo builder before starting the silo. The provider captures the value at construction; a restart applies configuration changes.

With this option enabled, the provider asks each missing silo for the content hash of its local manifest. A matching entry in the local hash cache supplies the manifest immediately. Otherwise, the provider requests the manifest by hash, verifies its content, and adds it to the cache.

Hashes use incremental SHA-256 over a fixed canonical traversal: an encoding version, sorted grain entries, then sorted interface entries. Each section and property collection starts with its count. Identifiers have a byte-length prefix; property keys and values have UTF-16 code-unit-length prefixes. A length of `-1` represents null or a default identifier, while `0` represents an empty value. Counts and lengths are signed 32-bit big-endian integers, and UTF-16 code units are written in big-endian order. This layout preserves exact identifier bytes and string contents, including invalid UTF-16, and gives equivalent content the same hash regardless of dictionary insertion order. Hashes of immutable manifest instances are memoized with weak keys, allowing the manifests to be collected when their owners release them.

Each publication creates a fresh cache containing its live manifests and local metadata. In-flight retrievals retain their original cache instance. A stale retrieval can populate that instance, while newer publications retain their own cache. The provider exposes the new manifest version before its new cache. Retrievals capture the cache before checking the version, so an older update which captures the newer cache also observes that its membership snapshot has been superseded.

## Peer repair and bounded waiting

When more than one active silo's manifest is missing, the enabled provider also probes up to three peers selected from a rotating ordered membership list. Direct retrieval starts alongside these probes.

Each probe has a one-second deadline shared by its hash-summary and manifest-update requests. Cached hashes can satisfy missing entries directly; otherwise the provider requests a complete update from the peer and verifies each candidate against the summary's hash. Successfully repaired entries are published immediately, including partial repairs. Direct requests for those entries are removed from the required completion set, allowing the remaining successful fetches to advance the manifest independently.

When direct retrieval supplies every missing manifest first, the provider cancels the optional peer attempts and publishes the direct results immediately. The successful direct path therefore completes independently of a slow peer-summary request.

The provider admits at most three concurrent local probe attempts. Completion, timeout, and caller cancellation release the attempt's slot immediately, allowing later retries to proceed. The one-second deadline cancels the request token and ends local waiting; Orleans signals cancellation to the peer using the ordinary RPC path. Direct retrieval continues alongside these attempts. Late responses leave the completed attempt's result unchanged, and late failures are observed and logged. Retry selection rotates through active peers, with retries paced by the five-second delay or a newer membership snapshot.

## Compatibility, rollout, and rollback

Silos serve hash requests on demand, including when they are configured for direct retrieval. Providers using the default content-addressed mode compute and reuse content hashes as manifests become available.

During a rolling deployment, enabled silos try hash retrieval and fall back to the established direct RPC when a peer rejects the newer method or its hash request fails. An invalid or unavailable hash-addressed body also triggers direct retrieval. Independent remote cancellation follows that compatibility path; cancellation of the local request propagates to the caller. Peer-repair failures leave direct retrieval responsible for filling the missing entries.

Upgraded silos adopt content-addressed retrieval at startup while existing silos continue using their deployed implementation. Unsupported hash requests can produce transient exception logs and extra requests during the upgrade; successful direct fallback supplies the required metadata. Individual silo-manifest RPCs retain the configured system response timeout, while optional peer-summary/update attempts have a one-second deadline.

Observe manifest retrieval and peer-probe diagnostics during joins and restarts. Debug logs distinguish hash fallback, peer timeout, occupied probe slots, and late failures. Warnings identify failed direct fetches. To select direct retrieval, set the option to `false` and restart the affected silos; they continue answering enabled peers' hash requests on demand.

## Measuring retrieval

The `Microsoft.Orleans` meter exposes [manifest retrieval instruments](../host/monitoring/metrics-catalog.md#cluster-manifests). Compare a canary with silos using direct retrieval during equivalent joins and restarts:

| Signal | Interpretation |
|---|---|
| `orleans-manifest-cache-lookups`, split by `result` and `source` | The hit fraction shows how often known content satisfies direct hash requests or peer-summary lookups. |
| `orleans-manifest-fallbacks`, split by `reason` | Counts hash retrievals which proceed through the direct RPC after an error, missing body, or content mismatch. |
| `orleans-manifest-peer-probes`, split by `status` | Shows completed local attempts, timeouts, cancellation, errors, and admission skips. Cancellation also includes optional probes superseded by successful direct retrieval. |
| `orleans-manifest-peer-repairs` | Counts missing silo entries supplied by successfully published peer repairs. Repeated summaries contribute each repaired entry once per publication. |
| `orleans-manifest-retrieval-duration`, split by `mode` and `status` | Measures each local silo-manifest retrieval in milliseconds, including cache lookup, fallback, and terminal cancellation or failure. |

Attributes use fixed categories, keeping time-series cardinality stable across silo restarts. The direct mode records retrieval duration while the hash and peer counters reflect content-addressed retrieval activity. Use these metrics alongside serialized message sizes and silo CPU/memory to evaluate the benefit for the service's manifest sizes and mix of application versions.

See [cluster membership](cluster-management.md) for membership transitions and [rolling version skew](rolling-version-skew.md) for how manifest metadata drives interface-version selection.
