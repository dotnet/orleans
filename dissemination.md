# Topic-Based Dissemination for Orleans

This document describes a design for an internal Orleans dissemination subsystem. It distills lessons from exploring anti-entropy, epidemic gossip, content-addressed manifests, and Plumtree-style broadcast trees. It is intended as the basis for a fresh implementation, not a description of any prototype branch.

## Goals

- Provide one internal topic-based dissemination subsystem for silo-to-silo runtime state.
- Reduce all-to-all communication for high-rate state such as deployment load statistics.
- Preserve correctness backstops for liveness and placement.
- Keep the implementation deterministic and highly testable.
- Keep public behavior default-safe and opt-in during rollout.
- Support rolling upgrades and mixed-version clusters without payload misinterpretation.
- Make batching a transport optimization which is orthogonal to Plumtree tree maintenance.
- Provide first-class observability through logs, metrics, and `DiagnosticListener` events.

## Non-goals

- This is not a general application pub/sub system.
- This is not reliable ordered delivery.
- This is not a replacement for the membership table.
- This is not a large-payload broadcast channel.
- This should not introduce new sockets or a separate networking stack.

## References

- Plumtree overview: https://www.bartoszsypytkowski.com/plumtree/
- F# HyParView/Plumtree gist: https://gist.github.com/Horusiath/84fac596101b197da0546d1697580d99

## Plumtree primer

Plumtree is a hybrid broadcast protocol. It combines tree-like efficient eager delivery with epidemic-style lazy repair.

| Concept | Meaning |
|---|---|
| Eager peer | Receives full payloads immediately. |
| Lazy peer | Receives `Advertise` announcements instead of full payloads. |
| Message id | Stable identity for an item, such as `(topic, origin silo, sequence)`. |
| `Advertise` | Lazy advertisement: "I have item X." |
| `Graft` | Repair request: "I missed item X; send it to me." |
| `Prune` | Eager-edge demotion: "This eager edge is redundant; make it lazy." |
| Payload cache | Short-lived cache used to serve grafts. |

Canonical Plumtree forms a broadcast tree per source/root over an underlying peer graph. In Orleans, every silo can be the root for its own updates. For example, a load-stat update from silo `S42` is rooted at `S42`, and its item id could be `(load, S42, statsTimestampTicks)`.

For this design, we intentionally generalize the model:

- Maintain one shared dissemination overlay per cluster, or per compatibility domain.
- Use topics and per-item ids to separate semantics.
- Let every topic reuse the same eager/lazy peer state.
- Keep per-topic merge and obsolescence logic outside the overlay.

This differs from pure per-source Plumtree. A shared overlay is simpler and lets membership, load, and manifest hints reuse the same efficient tree. The tradeoff is that edge adaptation is shared: a prune decision affects future traffic for more than one source/topic. Therefore pruning must be based on edge usefulness over a moving window, not on one duplicate item.

## Scenarios

### Deployment load statistics

Current load publication is the clearest high-rate scalability pressure. Each silo periodically publishes its own `SiloRuntimeStatistics` for placement and load balancing.

Design:

- Disseminate load updates as latest-wins topic values.
- Digest: `(topic = load, key = origin silo address string, version = statistics DateTime ticks, payload kind = SiloRuntimeStatistics)`.
- Coalesce pending load updates by origin silo.
- Keep only the latest statistics per origin while sends are in flight.
- Apply each value independently: newer timestamps replace older timestamps; equal timestamps are duplicates.
- Keep direct stale-stat repair as a correctness backstop for active silos.

Backstop:

- If an active silo has no stats or stale stats beyond the configured threshold, directly refresh that silo using the existing runtime statistics path.
- Do not remove active-silo stats solely because gossip missed an update. Membership status remains authoritative for removal.

Relevant existing locations:

- `src\Orleans.Runtime\Placement\DeploymentLoadPublisher.cs`
- `src\Orleans.Core\SystemTargetInterfaces\IDeploymentLoadPublisher.cs`
- `src\Orleans.Core\Statistics\SiloRuntimeStatistics.cs`
- `src\Orleans.Runtime\Placement\ISiloStatisticsChangeListener.cs`
- `src\Orleans.Runtime\Placement\ResourceOptimizedPlacementDirector.cs`
- `src\Orleans.Runtime\Configuration\Options\DeploymentLoadPublisherOptions.cs`

Expected edits:

- Add a load topic driver under `src\Orleans.Runtime\Dissemination`.
- Add load-topic options to `DeploymentLoadPublisherOptions`.
- Update `DeploymentLoadPublisher` to publish through the dissemination topic when enabled.
- Preserve the existing all-to-all path when dissemination is disabled.
- Preserve direct refresh and status-change removal semantics.

### Cluster membership updates

Membership dissemination is liveness-adjacent and must remain table-authoritative.

Design:

- Treat Plumtree dissemination as a best-effort accelerator for membership snapshots.
- The external membership table remains canonical.
- Receivers merge only through existing membership-table snapshot processing.
- Use full snapshots first; do not require deltas for correctness.
- Digest: `(topic = membership, key = "cluster", version = membership table version, payload kind = MembershipSnapshot)`.
- Older, equal, duplicated, and reordered snapshots are harmless because the membership manager already merges by version.

Backstop:

- Periodic table refresh remains the recovery path.
- Unsupported or failed dissemination should trigger or rely on table refresh.

Relevant existing locations:

- `src\Orleans.Runtime\MembershipService\MembershipTableManager.cs`
- `src\Orleans.Runtime\MembershipService\MembershipGossiper.cs`
- `src\Orleans.Runtime\MembershipService\MembershipSystemTarget.cs`
- `src\Orleans.Runtime\MembershipService\ClusterMembershipSnapshot.cs`
- `src\Orleans.Runtime\MembershipService\ClusterMembershipUpdate.cs`
- `src\Orleans.Core\SystemTargetInterfaces\IMembershipService.cs`
- `src\Orleans.Core\Configuration\Options\ClusterMembershipOptions.cs`

Expected edits:

- Add a membership topic driver under `src\Orleans.Runtime\Dissemination`.
- Add membership-topic options to `ClusterMembershipOptions`.
- Update `MembershipGossiper` to use the dissemination topic when enabled.
- Preserve existing `UseLivenessGossip` and table-refresh semantics.
- Preserve direct membership system-target notification as mixed-version fallback.

### Cluster manifest dissemination

Cluster manifests are different from load and membership. They are larger, less frequent, and naturally content-addressed. They should not be full-payload Plumtree broadcasts.

Design:

- Disseminate manifest hash summaries or hints only.
- Use content-addressed storage: `ManifestHash -> GrainManifest`.
- Digest or hash hint: `(topic = manifest, key = silo address string, version = manifest version, payload kind = ManifestHash)`.
- Pull missing manifests by hash from peers.
- Preserve current fully materialized client/gateway manifest APIs.
- Do not push full manifests via eager gossip.

Invariant:

- Full manifest payloads are fetched using bounded request/response APIs, preferably by hash, with payload-size and paging support if needed.
- Full manifest gossip should be prohibited because manifests can exceed normal gossip payload limits.

Backstop:

- Existing direct manifest fetch remains the correctness path.
- If a hash cannot be resolved, fall back to direct `GetSiloManifest`.

Relevant existing locations:

- `src\Orleans.Runtime\Manifest\SiloManifestProvider.cs`
- `src\Orleans.Runtime\Manifest\ClusterManifestProvider.cs`
- `src\Orleans.Runtime\GrainTypeManager\ClusterManifestSystemTarget.cs`
- `src\Orleans.Core\Manifest\IClusterManifestProvider.cs`
- `src\Orleans.Core\Manifest\IClusterManifestSystemTarget.cs`
- `src\Orleans.Core.Abstractions\Manifest\ClusterManifest.cs`
- `src\Orleans.Core.Abstractions\Manifest\GrainManifest.cs`

Expected edits:

- Add manifest hash/CAS support under `src\Orleans.Runtime\Dissemination`.
- Add additive internal manifest system-target methods for hash summary and fetch-by-hash.
- Update `ClusterManifestProvider` to use hash summaries before direct fetch.
- Preserve existing client-facing manifest update APIs.

## Files and project locations

Core contracts and public options:

- `src\Orleans.Core\SystemTargetInterfaces\IDisseminationSystemTarget.cs`
- `src\Orleans.Core\SystemTargetInterfaces\IDeploymentLoadPublisher.cs`
- `src\Orleans.Core\SystemTargetInterfaces\IMembershipService.cs`
- `src\Orleans.Core\Manifest\IClusterManifestSystemTarget.cs`
- `src\Orleans.Core\Runtime\Constants.cs`
- `src\Orleans.Core\Configuration\Options\ClusterMembershipOptions.cs`
- `src\api\Orleans.Core\Orleans.Core.cs`

Runtime implementation:

- `src\Orleans.Runtime\Dissemination\*.cs`
- `src\Orleans.Runtime\Hosting\DefaultSiloServices.cs`
- `src\Orleans.Runtime\Placement\DeploymentLoadPublisher.cs`
- `src\Orleans.Runtime\MembershipService\MembershipGossiper.cs`
- `src\Orleans.Runtime\GrainTypeManager\ClusterManifestSystemTarget.cs`
- `src\Orleans.Runtime\Manifest\ClusterManifestProvider.cs`
- `src\Orleans.Runtime\Configuration\Options\DeploymentLoadPublisherOptions.cs`
- `src\api\Orleans.Runtime\Orleans.Runtime.cs`

Tests:

- `test\Orleans.Runtime.Internal.Tests\Dissemination\*.cs`
- Functional tests near existing membership, manifest, and placement/load test suites.
- CsCheck is already available through `Directory.Packages.props`.

Serialization/API constraints:

- All cross-silo DTOs need `[GenerateSerializer]` and stable `[Id(n)]` values.
- Never renumber existing serialization ids.
- Public option changes require `src\api` baseline updates.
- Prefer additive wire changes for rolling upgrades.

## Architecture

### Layering

```text
Topic API
  Load topic
  Membership topic
  Manifest topic

Coalescers
  Latest load per silo
  Latest membership snapshot/version
  Manifest hash hints

Shared dissemination substrate
  Topic registry
  Deterministic spanning tree
  Digest anti-entropy repair
  Capability and fallback
  Batch construction

Transport
  Orleans system-target calls
  Legacy topic-specific fallback calls
```

### Deterministic spanning trees

The dissemination substrate maintains two rooted k-ary trees from cluster membership:

- `AllMembers`: silos in `Joining`, `Active`, `ShuttingDown`, or `Stopping`.
- `ActiveMembers`: silos in `Active`.

Both tree inputs are immutable arrays sorted by the natural deterministic `SiloAddress` sort order, not by an additional hash. When cluster membership changes, both cached topologies are rebuilt from the latest member arrays. Each topic declares which membership scope it uses:

- Membership snapshots use `AllMembers`, matching existing membership gossip eligibility.
- Deployment load statistics use `ActiveMembers`, matching existing load publisher behavior.
- Manifest exchange remains active-only and pull-based.

For a broadcast rooted at `root`, treat `root` as logical tree index `0` by rotating the selected sorted array during child lookup. For fanout `k`, the node at tree index `i` owns child tree indexes `k * i + 1` through `k * i + k`.

The all-members tree can include silos which are not reachable yet or are leaving. Active participants still need to be known dissemination-capable before a publish uses the tree. Transient unavailability for `Joining`, `ShuttingDown`, or `Stopping` participants does not fail publication: missed subtrees are repaired by anti-entropy or removed by cluster membership.

### Topic API

A topic driver supplies value semantics. The substrate supplies transport, batching, duplicate suppression, and repair.

Example shape:

```csharp
internal interface IDisseminationTopic
{
    string Name { get; }

    DisseminationMembershipScope MembershipScope { get; }

    DisseminationTopicOptions Options { get; }

    IReadOnlyList<DisseminationTopicDigest> GetDigests();

    int CompareVersion(DisseminationTopicDigest left, DisseminationTopicDigest right);

    bool IsObsolete(DisseminationTopicDigest digest);

    ValueTask<DisseminationValue?> GetValue(
        DisseminationTopicDigest digest,
        DisseminationTopicDigest? peerDigest,
        CancellationToken cancellationToken);

    ValueTask<DisseminationApplyResult> ApplyValue(
        DisseminationValue value,
        CancellationToken cancellationToken);

    ValueTask OnFallbackRequired(
        SiloAddress? peer,
        DisseminationTopicDigest digest,
        CancellationToken cancellationToken);
}
```

Digest and value shapes:

```csharp
internal readonly record struct DisseminationTopicDigest(
    string Key,
    long Version);

internal sealed class DisseminationValue
{
    public DisseminationTopicDigest Digest { get; init; }
    public SiloAddress Root { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
}
```

`DisseminationTopicDigest` is the topic-local payload-free summary exchanged during anti-entropy. It identifies one version of one logical value within the enclosing topic:

| Property | Meaning |
|---|---|
| `Key` | Topic-local string key. Membership uses `"cluster"`; load statistics use the source silo's `SiloAddress.ToParsableString()` value. |
| `Version` | Monotonic version for this topic/key stream. Higher versions supersede lower versions according to topic comparison rules. |

`DisseminationValue` carries a topic-local digest plus routing and payload data. Gossip and repair responses group values by topic, so the topic is not repeated on each value:

| Property | Meaning |
|---|---|
| `Digest` | The `DisseminationTopicDigest` for the value being sent. |
| `Root` | Silo which rooted this tree broadcast. This is routing metadata and is not necessarily the same as the value key. |
| `ExpiresAt` | Time after which the value should not be applied or forwarded. |
| `Payload` | Serialized topic-specific value. The topic owns deserialization, validation, version comparison, and application. |

Apply result:

```csharp
internal enum DisseminationApplyResult
{
    Applied,
    Duplicate,
    Obsolete,
    Rejected,
}
```

### Transport API

Use distinct control operations instead of overloading one generic one-way payload. This prevents old silos from misinterpreting control messages as full data payloads.

```csharp
internal interface IDisseminationSystemTarget : ISystemTarget
{
    Task<DisseminationCapabilityResponse> GetCapabilities(
        DisseminationCapabilityRequest request);

    Task PushGossip(DisseminationGossipBatch batch);

    Task PushAdvertise(DisseminationAdvertisementBatch batch);

    Task Graft(DisseminationGraftBatch request);

    Task Prune(DisseminationPruneMessage message);
}
```

Capabilities must be topic-aware. A peer is only capable for a topic if:

- The dissemination system target exists.
- The topic is registered.
- The topic is enabled.
- The peer supports the requested protocol version and payload kinds.

Unknown peers must use legacy topic-specific fallbacks or existing direct repair paths.

### Time and scheduling

All time-dependent behavior must use injected time:

- Inject `TimeProvider` into the substrate.
- Use `TimeProvider.GetUtcNow()` for cache expiry, graft due times, failure backoff, and stale checks.
- Use `TimeProvider.CreateTimer(...)` or a small Orleans timer abstraction backed by `TimeProvider`.
- Tests should use `FakeTimeProvider` to advance time deterministically.

Do not use `DateTime.UtcNow` directly in protocol state.

State mutation must be serialized:

- Route system-target inbound operations, timer ticks, graft timeouts, cache pruning, and membership-change events through one serialized queue per overlay.
- Avoid mutating protocol state concurrently from thread-pool timer callbacks and system-target turns.

## Batching

Batching is orthogonal to Plumtree. A batch is a transport envelope; correctness remains per item.

### Batch wire shape

```text
DisseminationGossipBatch
  Sender
  ValuesByTopic[topic][]

DisseminationAdvertisementBatch
  Sender
  Advertisements[]

DisseminationGraftBatch
  Sender
  ItemIds[]
```

Each item, advertisement, and graft request carries its topic and id. The batch has no independent delivery semantics.

### Per-item rules

- Deduplicate per item id.
- Cache payloads per item id.
- Apply topic merge rules per item.
- Send lazy advertisements per item.
- Graft missing payloads per item.
- Retry unavailable grafts per item.

### Prune rules with batches

`Prune` is edge-level, so it must not fire because one item in a batch was duplicate.

Use edge usefulness over a moving window:

- If a peer sends a batch with at least one new, non-obsolete item, keep the edge eager.
- If a peer repeatedly sends batches with zero useful items, demote that edge to lazy.
- Use thresholds such as duplicate-only batch count, duplicate ratio, and minimum observation window.
- Do not let one partially duplicate multi-topic batch prune an edge needed by other topics.

This is the key rule which keeps batching orthogonal to Plumtree.

### Topic coalescers

Each topic may coalesce before items enter the shared overlay.

Load metrics:

- Pending map: `SiloAddress -> latest SiloRuntimeStatistics`.
- Keep only the newest timestamp per silo.
- Flush on max delay, max item count, or max bytes.

Membership:

- Pending snapshot by latest membership version.
- Full snapshots first.
- Table refresh remains authority.

Manifest:

- Pending hash hints by silo/version/hash.
- No full manifest payloads in gossip batches.
- Fetch missing full payloads by hash.

### Fairness and backpressure

The batcher should enforce:

- Max items per batch.
- Max serialized bytes per batch.
- Max per-peer queued advertisements.
- Max per-topic queued items.
- Max flush delay.
- Max concurrent sends.

If batching across topics, construct batches using weighted round-robin or another fair scheduler so high-rate load statistics cannot starve membership or manifest hints.

If the implementation keeps independent per-topic loops initially, document that cross-topic fairness is not guaranteed and add a follow-up task before enabling cross-topic batches.

## Reliability and rolling upgrades

### Capability probing

Before sending Plumtree control operations to a peer:

1. Probe capabilities using an explicit request/response operation.
2. Include topic name and protocol version in the probe.
3. Cache capability with TTL.
4. Re-probe on membership changes, transient failures, and cache expiry.

Do not cache "unsupported" forever. A peer can upgrade during a rolling deployment, and a transient failure should not permanently downgrade it.

### Fallbacks

Fallback must be topic-specific:

- Load metrics fallback uses the existing deployment load publisher system target or direct stale-stat refresh.
- Membership fallback uses existing membership notification/table refresh.
- Manifest fallback uses direct manifest fetch or fetch-by-hash.

Do not route fallback through a new dissemination system target that older silos do not have.

### Oversize payloads

Oversize full payloads should be explicit failures:

- Emit a diagnostic event.
- Increment a metric.
- Trigger topic fallback if available.
- Never silently claim dissemination success.

Manifest full payloads should not be sent through gossip at all.

## Options

Global options:

- Enable/disable dissemination subsystem.
- Max concurrent sends.
- Capability cache TTL.
- Failure backoff.
- Max batch bytes.
- Max batch items.

Overlay options:

- Eager peer count.
- Lazy peer count.
- Initial jitter.
- Lazy advertise interval.
- Graft delay.
- Prune duplicate-only threshold.
- Prune observation window.
- Payload cache TTL.
- Max cached payload count/bytes.

Topic options:

- Enable topic.
- Max pending item count.
- Max coalescing delay.
- Stale item TTL.
- Topic fallback enabled.
- Topic-specific payload limit.

Validation:

- `PayloadCacheTtl > LazyAdvertiseInterval + GraftDelay + RequestTimeout`.
- Peer counts are positive when topic is enabled.
- Batch size limits are positive.
- Stale TTL is greater than publish/coalescing interval for latest-wins topics.

## Metrics and observability

Use three observability surfaces:

- Structured logs for warnings and unusual failures.
- `System.Diagnostics.Metrics` for low-cardinality counters and histograms.
- `DiagnosticListener` for detailed per-event diagnostics.

### Metrics

Meter name:

```text
Microsoft.Orleans.Dissemination
```

Recommended instruments:

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `orleans.dissemination.gossip.sent` | Counter | messages | `topic`, `kind` |
| `orleans.dissemination.gossip.received` | Counter | messages | `topic`, `kind` |
| `orleans.dissemination.values.sent` | Counter | values | `topic`, `kind` |
| `orleans.dissemination.values.applied` | Counter | values | `topic`, `result` |
| `orleans.dissemination.bytes.sent` | Counter | bytes | `topic`, `kind` |
| `orleans.dissemination.anti_entropy.exchanges` | Counter | operations | `direction`, `truncated` |
| `orleans.dissemination.anti_entropy.digests` | Counter | digests | `direction` |
| `orleans.dissemination.anti_entropy.values` | Counter | values | `direction` |
| `orleans.dissemination.prunes.sent` | Counter | messages | `topic` |
| `orleans.dissemination.grafts.sent` | Counter | items | `topic` |
| `orleans.dissemination.grafts.served` | Counter | items | `topic`, `result` |
| `orleans.dissemination.cache.entries` | ObservableGauge | entries | `topic` |
| `orleans.dissemination.queue.depth` | ObservableGauge | items | `topic`, `queue` |
| `orleans.dissemination.send.duration` | Histogram | milliseconds | `topic`, `kind` |
| `orleans.dissemination.convergence.latency` | Histogram | milliseconds | `topic` |
| `orleans.dissemination.fallbacks` | Counter | operations | `topic`, `reason` |
| `orleans.dissemination.payload.dropped` | Counter | values | `topic`, `reason` |

Avoid unbounded metric cardinality:

- Do not tag metrics with `SiloAddress`.
- Do not tag metrics with message ids.
- Use `topic`, `kind`, and coarse `result`/`reason` tags only.
- Put peer and message details in diagnostic events instead.

### DiagnosticListener events

Listener name:

```text
Microsoft.Orleans.Dissemination
```

Recommended events:

| Event | Purpose |
|---|---|
| `Dissemination.GossipSend` | Full payload batch sent. |
| `Dissemination.GossipReceive` | Full payload batch received. |
| `Dissemination.AdvertiseSend` | Lazy advertisement batch sent. |
| `Dissemination.AdvertiseReceive` | Lazy advertisement batch received. |
| `Dissemination.PruneSend` | Edge demotion requested. |
| `Dissemination.PruneReceive` | Edge demotion processed. |
| `Dissemination.GraftSend` | Missing value repair requested. |
| `Dissemination.GraftReceive` | Repair request received. |
| `Dissemination.GraftServe` | Cached payload served or unavailable. |
| `Dissemination.ValueApply` | Topic value applied/duplicate/obsolete/rejected. |
| `Dissemination.ValueCoalesce` | Pending value replaced or batched. |
| `Dissemination.CacheEvict` | Payload evicted. |
| `Dissemination.CapabilityProbe` | Peer capability result. |
| `Dissemination.Fallback` | Topic fallback used. |
| `Dissemination.PayloadDrop` | Oversize or invalid payload dropped. |

Event payloads may include peer and value identity because `DiagnosticListener` is opt-in and diagnostic, but payloads must not include sensitive data beyond internal runtime identifiers.

Example event payload:

```csharp
internal sealed class DisseminationValueEvent
{
    public string Topic { get; init; }
    public SiloAddress LocalSilo { get; init; }
    public SiloAddress? Peer { get; init; }
    public string Key { get; init; }
    public long Version { get; init; }
    public string Result { get; init; }
    public int PayloadBytes { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

## Testing approach

Testing should exercise the protocol core separately from Orleans networking, then verify Orleans integration with small clusters.

### Unit tests

Protocol state machine:

- First full gossip caches payload and forwards to eager peers.
- Duplicate full gossip does not reapply and contributes to edge-prune scoring.
- A partially useful batch does not prune an edge.
- Repeated all-duplicate batches demote an edge to lazy.
- Lazy `Advertise` for an unseen item schedules delayed graft.
- Lazy `Advertise` for an obsolete item is ignored.
- `Graft` promotes a lazy edge to eager and serves cached payload.
- Graft retry tries alternate announcers.
- Cache expiry prevents serving expired payloads and triggers fallback/diagnostics.
- Oversize payloads emit events and use fallback.
- Capability cache expires and re-probes.

Overlay:

- Expander seed selection excludes self.
- Only active silos are selected.
- Peer sets are bounded.
- Ring selection is deterministic.
- Peer replacement occurs after membership changes.
- Prune/graft state is not overwritten by periodic reseeding.

Topic drivers:

- Load latest-wins semantics reject older and equal timestamps.
- Load coalescer keeps latest stats per silo.
- Membership snapshots merge only through membership manager.
- Manifest hash determinism is stable across dictionary ordering.
- Manifest fetch-by-hash validates expected hash.

Options:

- Invalid peer counts fail validation.
- Invalid TTL/delay combinations fail validation.
- Batch limits must be positive.
- Topic-specific stale TTLs are consistent with publish intervals.

Time:

- Use `FakeTimeProvider` to advance graft delay, lazy dispatch, cache expiry, failure backoff, capability TTL, and stale repair deterministically.

### Property-based tests with CsCheck

Use CsCheck to generate event schedules, overlays, failures, and item streams.

Core properties:

- At-most-once apply per item id.
- Newer load stats are never overwritten by older stats.
- Equal load stat timestamps do not reapply.
- Every non-obsolete item reaches every non-failed node eventually when the overlay remains connected and loss is eventually repaired.
- No item remains permanently pending when at least one announcer has a cached payload.
- No infinite prune/graft oscillation under stable membership and bounded loss.
- Capability fallback never sends control operations to peers known unsupported.
- Cache size remains bounded under arbitrary duplicate/rejected item streams.
- Batching preserves per-item semantics: applying items individually or in batches yields equivalent final topic state.

Generators:

- Cluster size: 1 to several hundred for regular property runs.
- Eager/lazy degree.
- Message origins and sequences.
- Topic mix.
- Duplicate, reorder, and loss rates.
- Peer failure/recovery schedules.
- Payload sizes near batch boundaries.
- Time advancement steps using `FakeTimeProvider`.

Longer-running property tests can run outside BVT if they are too expensive for the default test path.

### Deterministic simulations

Add in-memory simulations which drive the real protocol core:

- 2,000 logical nodes.
- Multiple origins publishing load stats.
- Mixed load, membership, and manifest-hint topics.
- Random eager send loss.
- Random lazy advertisement loss.
- Peer churn.
- Mixed capability peers.

Assertions:

- Full payload sends are near `O(N)` for each item, not `O(N * fanout)`.
- Advertise/graft traffic remains bounded.
- Convergence latency stays within expected repair intervals.
- Prune/graft stabilizes under fixed membership.
- Batching reduces network messages without changing final state.

### Functional and integration tests

Use `TestClusterBuilder` and existing Orleans test infrastructure.

Load statistics:

- Multi-silo cluster converges on active-silo load stats.
- Placement listeners still receive updates.
- A silo join is discovered and repaired.
- A stopped silo is removed only through status changes.
- Stale stats trigger direct refresh.

Membership:

- A silo missing intermediate versions catches up through disseminated snapshot or table refresh.
- Older/equal snapshots are ignored.
- Unsupported dissemination peer falls back to membership notification/table refresh.
- Dissemination disabled still converges through existing table refresh.

Manifest:

- Identical manifests produce identical hashes.
- Dictionary ordering does not affect hashes.
- Unknown hash resolution fetches payload once and reuses CAS.
- Client/gateway manifest APIs still return materialized manifests.
- Mixed-version peers fall back to direct manifest fetch.

Batching:

- Cross-topic batch applies items according to each topic's semantics.
- High-rate load updates do not starve membership updates.
- Batch size limits split batches safely.
- A partially duplicate batch does not prune a useful edge.

Compatibility:

- New peer to old peer: no control envelopes are sent before capability is known.
- Old peer fallback uses existing topic-specific APIs.
- Capability cache expiration allows upgraded peers to become Plumtree-capable.

### Test plan

1. Unit-test DTO validation, option validation, coalescers, overlay selection, and protocol state transitions.
2. Add CsCheck property tests for core invariants and batch equivalence.
3. Add deterministic simulations for 2,000 logical nodes and mixed-topic traffic.
4. Add focused functional tests for load, membership, and manifest scenarios.
5. Add mixed-version/fallback simulations using fake transports.
6. Run focused tests on `net8.0` and `net10.0`.
7. Run full `dotnet build Orleans.slnx -bl`.

Suggested commands:

```powershell
dotnet test .\test\Orleans.Runtime.Internal.Tests\Orleans.Runtime.Internal.Tests.csproj --framework net8.0 --filter "Category=Dissemination" -- -parallel none -noshadow
dotnet test .\test\Orleans.Runtime.Internal.Tests\Orleans.Runtime.Internal.Tests.csproj --framework net10.0 --filter "Category=Dissemination" -- -parallel none -noshadow
dotnet build .\Orleans.slnx -bl
```

## Area-by-area design critique and mitigations

### Plumtree model

Critique: Pure Plumtree is per-source. A shared overlay can couple prune/graft decisions across topics and sources.

Mitigation: Document the shared-overlay choice explicitly. Use edge usefulness thresholds rather than single-message prune. Keep per-item dedup and merge semantics separate from edge adaptation.

### Time and determinism

Critique: Wall-clock time makes graft, cache, backoff, and stale repair tests flaky.

Mitigation: Require injected `TimeProvider` and `FakeTimeProvider` tests. No protocol state should call `DateTime.UtcNow` directly.

### Batching

Critique: Per-item repair and link-level prune can conflict.

Mitigation: Treat batches as envelopes only. Keep item-level ids, caches, grafts, and merge results. Prune only after an edge-level duplicate/obsolete threshold over a window.

### Manifest dissemination

Critique: Full manifests can exceed gossip payload limits.

Mitigation: Disseminate only hash hints/summaries. Fetch full manifests by hash through bounded request/response APIs and preserve direct fetch fallback.

### Rolling upgrade

Critique: Overloaded control messages can be misread by older silos.

Mitigation: Use distinct system-target methods and topic-aware capability probing. Use legacy topic-specific fallback until capability is known.

### Metrics

Critique: Per-peer metric tags would explode cardinality.

Mitigation: Keep metrics low-cardinality with topic/kind/result tags. Put peer and item details in `DiagnosticListener` events.

### Load latest-wins semantics

Critique: Lazy repair can request obsolete load items.

Mitigation: Topic driver checks obsolescence before grafting and before applying. Direct stale-stat repair handles missed active-silo data.

### Membership authority

Critique: Gossip must not become the liveness authority.

Mitigation: Dissemination is only an accelerator. The membership table and existing snapshot merge logic remain authoritative.

## Rollout

1. Implement substrate disabled by default.
2. Enable load statistics dissemination in targeted performance tests.
3. Add manifest hash/CAS optimization.
4. Enable membership snapshot dissemination only after mixed-version and failure tests pass.
5. Measure message counts, convergence latency, stale repair rates, and placement behavior at scale.
6. Revisit defaults after production-like performance and reliability data.
