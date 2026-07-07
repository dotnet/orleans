# Efficient Broadcast for Orleans Runtime State

## Status

This document describes the current efficient-broadcast branch. The branch implements deterministic fixed-tree broadcast, digest-based anti-entropy repair, value-oriented wire contracts, status-and-age-prioritized topology ordering, dynamic fanout, per-topic membership scopes, level-aware randomized repair peer selection, membership snapshot diffs, manifest peer-fill, stale-only anti-entropy probes, and per-peer outbound gossip coalescing. Dissemination is currently opt-in: the global `DisseminationOptions.Enabled` flag and each topic's `DisseminationTopicOptions.Enabled` flag default to `false`, so existing direct publication/gossip paths remain the default unless dissemination is explicitly enabled.

## Problem statement

Orleans has several pieces of silo-to-silo runtime state which need to spread quickly through the cluster: deployment load statistics, membership snapshots, and manifest-related metadata. Some of this state is high-rate, especially load statistics, and all-to-all publication scales poorly as cluster size grows.

The goal is to reduce routine fanout while preserving correctness backstops. These payloads are monotonically versioned values. A receiver can decide whether an incoming value is newer, duplicate, obsolete, or invalid using topic-specific semantics. Deterministic broadcast plus periodic anti-entropy fits that value model well.

## Challenges and constraints

| Constraint | Implication |
|---|---|
| Relevant silos can address each other directly | Dissemination can build trees from cluster membership. |
| Membership and fault detection are authoritative | Dissemination uses membership to choose peers and relies on existing liveness logic to remove failed silos. |
| Values are monotonic per `(topic, key, payloadKind)` | Topic version comparison provides duplicate suppression. |
| Topics own value semantics | The protocol handles routing, validation, and batching. Topics compare versions, materialize values, apply values, and perform fallback. |
| Rolling upgrades are best-effort | New dissemination messages are attempted directly. Peers which cannot process them fail or reject them, and anti-entropy plus existing authoritative refresh paths repair after the temporary mismatch clears. |
| Payloads are bounded | Oversize payloads are rejected and topic fallback is used during publication. Gossip and anti-entropy batches are bounded by item count and total payload bytes. |
| Delivery is best-effort | Tree send failures are repaired by anti-entropy on its periodic cadence. |
| Different runtime systems have different membership eligibility | The protocol maintains both active-only and all-member topologies and lets each topic choose. |

Scope: this design covers Orleans internal runtime replication for bounded, monotonic values with existing correctness backstops.

## Usage scenarios

| Scenario | Shape | Membership scope | Authority | Why it is distinct |
|---|---|---|---|---|
| Deployment load statistics | One latest-wins value per silo. Key is the source `SiloAddress.ToParsableString()`. Version is `SiloRuntimeStatistics.DateTime.Ticks`. | `ActiveMembers` | Existing placement/load publisher state. | High-rate, small payloads, many independent keys. The main scalability win is bounded routine refresh fanout. |
| Membership snapshots | One singleton cluster value. Key is `"cluster"`. Version is `MembershipTableSnapshot.Version.Value`. | `AllMembers` | Membership table and membership manager remain authoritative. | Liveness-adjacent, so dissemination is only an accelerator. Existing table refresh/gossip fallback remains the safety path. |
| Cluster manifests | Content-addressed metadata. Hash identifies a `GrainManifest`; the cluster manifest is a map of active silos to manifests. | Active-only pull today | Existing manifest provider remains authoritative. | Larger and less frequent than load/membership. The current design optimizes fetch-by-hash and cache reuse. |

The key distinguishing factors are value cardinality, update rate, payload size, diffability, and correctness authority. Load has many small latest-wins values. Membership has one correctness-sensitive stream which can potentially be represented as diffs. Manifests are large, mostly identical across hosts, and better handled by content-addressed pull with direct fallback.

## Value model

Each disseminated value is summarized by a payload-free `DisseminationDigest`:

```text
(topic, key, version, payloadKind)
```

`DisseminationDigest` properties:

| Property | Meaning |
|---|---|
| `Topic` | Logical dissemination topic, such as membership or deployment load. |
| `Key` | Topic-defined string identifying one monotonic value stream within the topic. Examples: `"cluster"` for membership and `SiloAddress.ToParsableString()` for load statistics. |
| `Version` | Topic-defined monotonic version. The protocol treats it as opaque except for sorting; topics decide whether one digest is newer than another. |

The payload envelope is `DisseminationValue`:

| Property | Meaning |
|---|---|
| `Digest` | The resulting value identity/version represented by the payload. |
| `Root` | The silo which originated the update and acts as the temporary virtual root for fast-path forwarding. The value key lives in `Digest.Key`. |
| `ExpiresAt` | A topic TTL after which the value is no longer useful and is dropped as stale. |
| `Payload` | Serialized topic payload. Deployment load uses full values. Membership can use either a full snapshot or a diff payload while keeping `Digest.Version` as the post-apply version. |

Using a plain string key keeps the common wire model simple and topic-neutral. Topic implementations validate and parse their own keys at the boundary.

## Membership scopes and topology

The transport returns a `DisseminationMembership` containing two immutable, deterministically ordered arrays:

- `AllMembers`: silos in `Joining`, `Active`, `ShuttingDown`, or `Stopping`.
- `ActiveMembers`: silos in `Active`.

Topologies order participants by status, age, and address:

```text
Status: Active > Joining > ShuttingDown > Stopping
Age: oldest first
Tie-breaker: SiloAddress.CompareTo
```

For `ActiveMembers`, the status component is constant, so the order is oldest active silos first followed by `SiloAddress`. For `AllMembers`, status rank keeps active silos near the top of all-member trees and pushes joining or leaving silos toward leaf positions. Age ordering then places longer-lived silos nearer the top, improving the probability that interior forwarding nodes are both available and likely to have the requested values. Age should use the membership entry's `StartTime` when the ordered membership snapshot includes it. `SiloAddress.Generation` is the deterministic age proxy available from the address itself and is already the first component of `SiloAddress.CompareTo`.

`DisseminationProtocol` caches one `ParticipantTopology` per scope and rebuilds a topology when its participant set or order changes. Each topology represents a fixed k-ary forest over the ordered array. A broadcast originator acts as a temporary virtual root above that forest.

Each topic declares a `DisseminationMembershipScope`:

- Membership snapshots use `AllMembers`, matching existing membership gossip eligibility.
- Deployment load statistics use `ActiveMembers`, matching existing load publishing behavior.
- Manifest exchange remains active-only and pull-based today.

The all-member tree can include silos which are joining or leaving. Publication does not preflight peers. Transient send failures, temporary version mismatches, and membership skew are repaired by anti-entropy or made irrelevant by membership changes.

## Fast-path algorithm: fixed deterministic tree broadcast

For a topic value:

1. Choose the topic's membership scope and load the corresponding ordered topology.
2. Ensure the publishing `Root` is present in the participant set.
3. Use the ordered array directly as a fixed k-ary forest. The first `k` participants are the top-level nodes beneath a virtual root.
4. Evaluate the fanout factor `k` from the current participant count.
5. The participant at fixed index `i` owns children at indexes `k * (i + 1)` through `k * (i + 1) + k - 1`.
6. The originator sends to the top-level nodes and to its own fixed children, de-duplicating targets and excluding itself.
7. Each receiver validates topic, payload kind, payload size, TTL, and obsolescence.
8. The topic applies the value using its own version semantics.
9. A receiver forwards to its own fixed children when the value was newly applied, excluding the originator if it appears in that child set. Duplicate, obsolete, invalid, or expired values terminate at the receiver.

The child calculation is:

```text
topLevel = indexes 0 ... fanout - 1
firstChild(index) = fanout * (index + 1)
children(index) = firstChild(index) ... firstChild(index) + fanout - 1
originatorTargets = topLevel + children(index(root))
```

This gives every participant the same fixed topology when their membership view agrees. The originator sends to at most `2 * fanout` distinct peers, and every other forwarding participant sends to at most `fanout` peers.

Outbound tree sends are queued per peer before transport. The queue coalesces values by `(topic, key)` and keeps only the newest version for each stream, then flushes when the earliest topic `MaxCoalescingDelay` expires or when batch/item limits are reached. This preserves monotonic latest-wins semantics while allowing different originators' values to share the same topic-grouped `DisseminationGossipBatch` on relay nodes.

### Critique of the fixed-tree fast path

The fixed-tree design improves the common deployment-load case. Each silo periodically originates its own load value, so a fixed forest keeps every originator's direct fast-path target set bounded and stable while preserving deterministic reachability.

The subtle correctness requirement is that the fixed topology must behave like a forest under a temporary virtual root. If the design used a single fixed root and the originator only sent to that root's children, the fixed root itself would miss updates originated by other silos. Defining the first `fanout` ordered participants as top-level virtual-root children gives complete coverage: the top-level nodes cover the fixed forest, while the originator also sends to its own children to cover the subtree below the originator when its parent excludes it.

The trade-off is relay concentration. Top-level and high-level participants forward more updates than leaves because every originator uses the same fixed forest. That concentration is predictable, bounded by fanout per update, and balanced by anti-entropy for repair. If it becomes a hotspot in practice, future variants can rotate the virtual top-level window on a slow cadence or per topic while keeping per-originator direct send sets bounded.

### Dynamic fanout factor

The fanout factor should scale with participant count. The overlay option can expose a code-configured selector:

```csharp
public Func<int, int> FanOutFactor { get; set; }
```

The function receives the current participant count for the selected topology and returns the k-ary forest fanout. A fixed fanout is still expressible as `static _ => 3`.

For a virtual-root k-ary forest, the number of participants covered within `h` hops is:

```text
capacity(h, k) = k + k^2 + ... + k^h
```

Therefore, using `sqrt(n)` for a 2-hop target and `cbrt(n)` for a 3-hop target is sound and slightly conservative because the lower-order terms add extra capacity. Example bounded selectors:

```csharp
// Approximately targets 2 hops, bounded to [4, 32].
static count => (int)Math.Ceiling(Math.Max(4, Math.Min(Math.Sqrt(count), 32)));

// Approximately targets 3 hops, bounded to [4, 32].
static count => (int)Math.Ceiling(Math.Max(4, Math.Min(Math.Cbrt(count), 32)));
```

With a maximum fanout of 32, the 2-hop target covers up to `32 + 32^2 = 1,056` participants and the 3-hop target covers up to `32 + 32^2 + 32^3 = 33,824` participants. Larger trees continue to work with additional hops. The evaluated value should be clamped to a positive value and target enumeration should still cap sends by participant count.

A `Func<int, int>` option is appropriate for code-based configuration. Configuration-file scenarios should also have a bindable preset or numeric fallback, such as a `TargetHopCount` plus `MinFanOutFactor` and `MaxFanOutFactor`, which can build the function during options post-configuration.

## Repair algorithm: anti-entropy

Anti-entropy periodically compares local digests with selected peers and transfers newer values from the receiver to the requester.

Current branch behavior:

1. Enumerate enabled topics and their local `DisseminationTopicDigest` values whose `(topic, key)` streams have not received a recent update.
2. Select repair peers per topic using that topic's membership scope.
3. Send `DisseminationAntiEntropyRequest` containing a per-topic map of stale local digests. If no stale digests remain for a peer, skip that peer for the round.
4. The receiver maps remote digests by key within each requested topic.
5. For each requested local digest key, if local state is newer than the requester digest, materialize a value.
6. Return values up to `MaxBatchItems` and `MaxBatchBytes`, where `MaxBatchBytes` is the sum of payload byte lengths rather than exact serialized envelope size, setting `Truncated` if more values remain.
7. The requester applies returned values locally.

Each topic has an `ExpectedUpdateCadence` used to decide when a `(topic, key)` stream is stale enough to probe. Deployment load statistics default to 2 seconds and membership snapshots default to 10 seconds. Recent tree or repair updates suppress digest probes for that stream until the cadence elapses. Omitted digests mean "not probing this stream in this round" rather than "missing this stream", which keeps anti-entropy from returning values for streams that are already receiving regular fast-path updates. When a topic knows that a stream should exist but has no local value, it can send an explicit low-watermark digest for that key; deployment load does this for active silos with missing local statistics.

Sorted digest iteration makes truncation deterministic, so repeated stale repair rounds converge over time.

### Level-aware randomized repair peer selection

Repair-peer selection is level-aware and randomized, using the same fixed topic topology and evaluated fanout factor.

For each topic repair round:

1. Build the same participant topology used by that topic.
2. Derive a per-topic, per-round pseudo-random salt using lightweight deterministic hashing with enough variation to spread contacts over time.
3. Map the local silo into the fixed forest's level-order indexes.
4. Prefer candidates from the previous tree level, meaning one level closer to the virtual root. To keep fanout bounded, sample from a fanout-sized window in that previous level near the local node's parent group.
5. If the local silo is in the top level, sample from the same top level excluding itself, so top-level nodes cross-check each other.
6. Select up to `AntiEntropyPeerCount` unique peers.

This keeps repair traffic bounded, sends repair probes against or sideways to the broadcast direction, and distributes repair contacts over time. It should remain testable by injecting or deriving the round salt.

## Diff-capable value exchange

Deployment load statistics materialize full latest-wins values. Membership snapshots reduce anti-entropy repair bytes using topic-specific diffs when the peer's digest is within retained history, and fall back to full snapshots otherwise.

Implemented repair design:

- Anti-entropy pull can exchange diffs when a topic can produce and apply them.
- The source of truth for a topic should be able to accept a peer digest and produce the smallest useful payload for that peer.
- `DisseminationDigest.Version` remains the target value version after the payload is applied.
- Membership diff and snapshot payloads are self-describing, so receivers can validate and reject unsupported payloads without widening the digest identity.
- A diff payload must include enough topic-specific base information for the receiver to decide whether it can apply the diff. If the receiver's local base is too old, too new, or missing, the topic rejects the diff and relies on anti-entropy or fallback to obtain a full value.

Membership is the primary diff candidate. The current implementation retains up to 32 membership snapshots keyed by membership version. When a peer digest is within the retained range, the responder sends the changes from the peer's version to the current version. If the peer is too far behind or the change history has been truncated, the responder sends a full `MembershipTableSnapshot`.

Deployment load statistics already use small latest-wins full payloads. Cluster manifests use content-addressed pull and can fetch a whole cluster manifest from a peer when that is cheaper than many per-silo fetches.

## Manifest handling

Cluster manifests are deliberately different from load and membership. Full manifests can be larger than normal dissemination payloads and are already content-addressable.

Current branch behavior:

- `ManifestHashCalculator` computes a canonical SHA-256 hash for each `GrainManifest`.
- `ClusterManifestSystemTarget` exposes hash-oriented methods such as `GetSiloManifestHash`, `GetSiloManifestByHash`, and `GetClusterManifestHashSummary`.
- `ClusterManifestProvider` caches `ManifestHash -> GrainManifest`.
- When a silo manifest is missing, the provider asks the target silo for its manifest hash, reuses a cached manifest when possible, fetches by hash otherwise, validates the hash before accepting the payload, and falls back to direct manifest fetch if the hash path fails.
- The provider removes non-active silos from the cluster manifest and only fetches manifests for active silos.

Accepted improvement:

- When many active manifests are missing, fetch a peer's whole cluster manifest or hash summary first.
- Because most hosts are expected to have the same cluster manifest, one active peer can often provide most missing manifests in one request.
- Validate each included `GrainManifest` by hash before caching or accepting it.
- Fall back to direct per-silo fetch only for members which are still missing, stale, invalid, or absent from the peer's cluster manifest.

This keeps manifests pull-based while reducing the number of direct per-member requests during convergence.

## Failure handling and fallback

Failure behavior:

- Send and anti-entropy request failures back off the peer temporarily using `FailureBackoff`.
- Publication queues tree sends without capability probing. If a peer cannot process a batch, the send fails or the receiver rejects unsupported values.
- Non-active participants in the all-member tree can be unavailable while publication proceeds.
- If dissemination is disabled, publish-time validation fails, payloads are oversize, or topic fallback is required before queueing, the producer uses the existing topic-specific safety path.
- After publication has queued successfully, tree-send failures, unsupported receivers, temporary mixed-version mismatches, or short-lived membership skew are repaired by anti-entropy and existing authoritative refresh paths rather than by immediate legacy send fallback.

## Algorithms and data structures

Important structures:

| Structure | Purpose |
|---|---|
| `DisseminationDigest` | Payload-free summary used for identity, version comparison, receiver validation, and anti-entropy. |
| `DisseminationValue` | Payload envelope used by tree gossip and anti-entropy responses. |
| `DisseminationMembership` | Transport snapshot containing ordered `AllMembers` and `ActiveMembers`. |
| `ParticipantTopology` | Cached ordered participants, index map, and participant set for one membership scope. |
| `DigestKey` | Protocol comparison key `(topic, key, payloadKind)` used to compare versions for the same value stream. |
| `AntiEntropyState` | Per-round local topics, digests, and selected peers grouped by topic. |
| `_failureBackoffUntil` | Short backoff for failed sends and anti-entropy requests. |

Important ordering rules:

- Participants are sorted by status rank, age, and `SiloAddress.CompareTo`.
- `ActiveMembers` has a constant status component, so older active silos appear before younger active silos.
- `AllMembers` uses status rank `Active`, `Joining`, `ShuttingDown`, `Stopping`, then age, then `SiloAddress.CompareTo`.
- Digests are sorted by topic, payload kind, key, and version for deterministic anti-entropy truncation.
- Topic key parsing is topic-owned. Invalid keys are rejected or treated as obsolete by the topic.

## Implementation overview

Core contracts:

- `src\Orleans.Core\SystemTargetInterfaces\IDisseminationSystemTarget.cs`: wire DTOs and system target API.
- `src\Orleans.Runtime\Dissemination\IDisseminationTopic.cs`: topic abstraction for digests, version comparison, materialization, apply, fallback, and membership scope.
- `src\Orleans.Runtime\Dissemination\IDisseminationTransport.cs`: runtime transport abstraction and `DisseminationMembership`.
- `src\Orleans.Core\Configuration\Options\DisseminationOptions.cs`: global, overlay, and per-topic options.

Runtime implementation:

- `src\Orleans.Runtime\Dissemination\DisseminationProtocol.cs`: tree broadcast, anti-entropy, topology cache, validation, and failure backoff.
- `src\Orleans.Runtime\Dissemination\DisseminationService.cs`: serialized protocol execution and anti-entropy lifecycle loop.
- `src\Orleans.Runtime\Dissemination\DisseminationSystemTarget.cs`: Orleans system target endpoint.
- `src\Orleans.Runtime\Dissemination\OrleansDisseminationTransport.cs`: system-target transport adapter and membership-scope construction.
- `src\Orleans.Runtime\Dissemination\DisseminationInstruments.cs` and `DisseminationEvents.cs`: metrics and diagnostic events.

Topic implementations:

- `src\Orleans.Runtime\Dissemination\DeploymentLoadStatisticsDisseminationTopic.cs`: latest-wins per-silo runtime statistics using active-member topology.
- `src\Orleans.Runtime\Dissemination\MembershipDisseminationTopic.cs`: singleton membership snapshot stream using all-member topology.
- `src\Orleans.Runtime\Dissemination\ManifestHashCalculator.cs`: canonical hash support for manifest pull/cache reuse.

Integration points:

- `src\Orleans.Runtime\Placement\DeploymentLoadPublisher.cs`: tries dissemination first, then falls back to direct stats update/refresh.
- `src\Orleans.Runtime\MembershipService\MembershipGossiper.cs`: tries dissemination first, then falls back to legacy membership gossip.
- `src\Orleans.Runtime\Manifest\ClusterManifestProvider.cs` and `src\Orleans.Runtime\GrainTypeManager\ClusterManifestSystemTarget.cs`: fetch manifest hashes first, reuse cached manifests by hash, validate hashes, and fall back to direct manifest fetch.
- `src\Orleans.Runtime\Hosting\DefaultSiloServices.cs`: registers dissemination service, transport, system target, and topics.

## Implemented follow-up decisions

1. Fixed-tree fast-path forwarding.
   - Order participants by status rank, age, and `SiloAddress`.
   - Use status rank `Active`, `Joining`, `ShuttingDown`, `Stopping` for `AllMembers`; `ActiveMembers` has a constant status component.
   - Preserve the supplied topology order when building `ParticipantTopology`.
   - Add a dynamic `FanOutFactor` selector and compute fanout from participant count.
   - Change tree child selection from root-rotated indexes to fixed forest indexes.
   - Build originator target sets from top-level nodes plus the originator's own children.
   - De-duplicate sends and exclude the originator from outbound target sets.
   - Add tests for top-level originators, deep originators, the first ordered participant, small clusters, all-member status/age ordering, dynamic fanout, and complete reachability.

2. Level-aware randomized anti-entropy peer selection.
   - Use the fixed forest levels from the topic's membership scope.
   - Add a testable repair-round salt source or derive one from time/round number.
   - Add helpers to compute k-ary tree level ranges and previous-level candidate windows.
   - Preserve per-topic membership scope selection.
   - Add tests for root, first-level, deeper-level, small-cluster, and bounded peer-count cases.

3. Peer-aware repair payload selection.
   - Add a topic API which accepts the peer digest when available and returns the best payload kind: diff if useful, full value otherwise.
   - Keep current full-value behavior as the default implementation path.
   - Ensure anti-entropy requests can ask for a payload relative to a remote digest.

4. Membership diff support.
   - Retain a bounded history of membership changes by membership version.
   - Add a membership diff payload kind and validate it at the receiver.
   - Apply diffs only when the local base version matches the payload requirements.
   - Fall back to full snapshots when history is missing, truncated, or incompatible.

5. Manifest convergence from peer cluster manifests.
   - When several active members are missing, query one or more active peers for their cluster manifest or hash summary.
   - Fill and validate all manifests available from that response.
   - Fall back to direct per-silo fetch for the remaining missing entries.

6. Stale-only anti-entropy.
   - Add per-topic `ExpectedUpdateCadence`.
   - Track recent `(topic, key)` updates and omit fresh streams from anti-entropy requests.
   - Treat omitted digests as unprobed streams rather than missing streams.
   - Let topics emit low-watermark digests for known missing streams which still need repair.

7. Per-peer outbound gossip coalescing.
   - Queue tree sends per peer before transport.
   - Coalesce pending values by `(topic, key, payloadKind)` and keep the newest version.
   - Flush by earliest topic coalescing delay, batch item/byte limits, or per-topic pending item limits.

## Future work, shortcomings, and trade-offs

The main trade-off is that the tree fast path reduces sends and relies on anti-entropy for repair. A child send failure can leave that child's subtree stale until anti-entropy repairs it. Convergence latency is bounded by anti-entropy cadence.

Other trade-offs and improvement areas:

- Level-aware randomized anti-entropy improves convergence under skew and correlated failures, but it is still probabilistic and should be monitored under real cluster churn.
- Fixed-tree forwarding bounds each originator's direct send set, while top-level and high-level participants carry more relay traffic than leaves.
- Status-and-age-prioritized ordering improves expected interior-node availability and value hit-rate while keeping ordering deterministic.
- Dynamic fanout trades direct send count for tree depth: a 2-hop target gives faster convergence with larger fanout; a 3-hop target lowers per-node sends and adds one relay hop.
- Diff repair reduces bytes for membership but adds history retention, self-describing payloads, base-version validation, and fallback paths.
- Removing capability probing keeps the fast path cheap but means unsupported or temporarily mismatched peers discover incompatibility by rejecting or failing actual dissemination messages.
- Deterministic trees require silos to mostly agree on membership. Anti-entropy repairs short-lived skew, but the fast path can duplicate or miss during disagreement.
- The protocol scope is monotonic versioned values. Ordered event streams would need separate sequencing, retention, and acknowledgment semantics.
- Manifest whole-cluster fetch can reduce request count but can also transfer more bytes than per-silo fetch in highly divergent clusters. Hash validation and fallback keep it safe.
- Tree sends are coalesced per peer before transport. Future scheduling work could respect `MaxConcurrentSends` across flushes and enforce cross-topic fairness under sustained overload.
- Once the protocol ships, wire-shape changes should become additive and versioned.
