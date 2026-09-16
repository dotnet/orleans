---
title: Persistent stream pulling architecture
description: Understand Orleans persistent stream providers, queue balancing, pulling agents, caches, cursors, pub-sub, and recovery.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Persistent stream pulling architecture

A persistent stream provider connects Orleans streams to a durable queue technology. Producers enqueue through an adapter. Pulling agents read queue partitions, cache batches, discover subscriptions, and deliver events through ordinary Orleans calls. Providers select silo-local system-target hosting or grain hosting through <xref:Orleans.Configuration.StreamPullingAgentOptions.HostingMode>.

This page describes the runtime mechanism. For stream APIs and provider selection, see the [streaming documentation](../../streaming/index.md).

The default system-target pipeline is:

```mermaid
flowchart LR
    Producer[Stream producer]
    Adapter[IQueueAdapter]
    Queue[(Durable queue)]
    Balancer[IStreamQueueBalancer]
    Manager[PersistentStreamPullingManager]
    Agent[Pulling agent SystemTarget]
    Cache[IQueueCache]
    PubSub[Stream pub-sub]
    Consumers[Grain/client consumers]

    Producer --> Adapter
    Adapter --> Queue
    Balancer --> Manager
    Manager --> Agent
    Agent --> Queue
    Agent --> Cache
    Agent <--> PubSub
    Cache --> Agent
    Agent --> Consumers
```

## Provider composition and lifecycle <a name="persistent-streams"></a>

<xref:Orleans.Providers.Streams.Common.PersistentStreamProvider> is the common implementation. A provider-specific <xref:Orleans.Streams.IQueueAdapterFactory> creates:

- an <xref:Orleans.Streams.IQueueAdapter> for enqueue and receive semantics;
- an <xref:Orleans.Streams.IStreamQueueMapper> for stream-to-queue mapping;
- an <xref:Orleans.Streams.IQueueAdapterCache> for per-agent caches; and
- optional failure handlers, filters, and backoff providers.

System-target hosting also resolves an <xref:Orleans.Streams.IStreamQueueBalancer>. Grain hosting uses a coordinator grain to manage placement.

During lifecycle initialization the provider resolves its named adapter factory and creates the adapter. At the active stage it initializes the pulling manager and starts agents. System-target hosting drains agents during provider shutdown. Grain hosting closes local admission and coordinator liveness checks, joins previously admitted manager commands, then lets grain deactivation perform final receiver cleanup and migration. In-flight start commands observe shutdown after coordinator calls, and queued starts are rejected before opening admission.

By default, pulling agents start automatically. Explicit grain-based and implicit subscriptions are both enabled.

API: <xref:Orleans.Providers.Streams.Common.PersistentStreamProvider>. Implementation: [provider lifecycle](https://github.com/dotnet/orleans/blob/main/src/Orleans.Streaming/PersistentStreams/PersistentStreamProvider.cs) and [provider options](https://github.com/dotnet/orleans/blob/main/src/Orleans.Streaming/PersistentStreams/Options/PersistentStreamProviderOptions.cs).

## Queue mapping and ownership <a name="streamqueuemapper-and-streamqueuebalancer"></a>

The queue mapper deterministically assigns a stream identity to a queue. All producers and consumers for a provider must use compatible mapping or events can be written to queues which no intended agent reads.

The queue balancer assigns queues to silos and publishes ownership changes. With the default system-target hosting, `PersistentStreamPullingManager` serializes those notifications, ignores stale sequences, and starts or stops one silo-local agent per assigned queue.

With <xref:Orleans.Configuration.StreamPullingAgentHostingMode.Grain>, one `PullingCoordinatorGrain` per named provider manages one stable `PullingAgentGrain` per queue. Agent identity preserves the provider name, queue prefix, numeric queue ID, and uniform hash. The grain directory resolves these identities and arbitrates activation registration.

```mermaid
flowchart LR
    Inventory[Provider queue inventory] --> Coordinator[Coordinator grain]
    Membership[Cluster membership] --> Coordinator
    Hosts[Silo-local lifecycle and liveness] --> Coordinator
    Coordinator -->|Probe with activation hint| Agents[Pulling-agent grains]
    Agents -->|Actual address and readiness| Coordinator
    Coordinator -->|Delayed rebalance RPC| Agents
    Agents -->|Flush then migrate on silo shutdown| Survivor[Surviving host]
```

The coordinator periodically probes every queue grain and groups successful observations by silo. Placement hints favor an agent's observed eligible host; queues without an eligible observed host receive a least-loaded-host hint. Calls to existing activations preserve their current location. A separate rebalance RPC requests migration and checks the observed activation address, so a stale request leaves a successor in place.

Placement considers grain type/interface compatibility, named-provider availability, running state, and queue inventory. Active membership is an observation of cluster state: a candidate can become unreachable before its eligibility response arrives. Expected transport failures are logged per candidate, and successful eligible responses remain available for placement. Caller cancellation terminates the overall eligibility request. Queue mappings must agree across hosts of a provider. <xref:Orleans.Configuration.StreamPullingAgentOptions.GrainHostingProbePeriod> controls the probe interval, which defaults to 30 seconds. Cluster membership and provider availability changes also request reconciliation. Silo-local liveness checks keep the coordinator available; after coordinator recovery, probes reconstruct its observed distribution while retaining healthy agent placements.

Optional balancing waits for <xref:Orleans.Configuration.StreamPullingAgentOptions.GrainHostingRebalanceDelay>, which defaults to one minute. Eligible-host changes, incomplete observations, and recovery restart stabilization. Once a complete observation shows persistent imbalance, the coordinator moves only the excess agents needed to bring queue counts within one of each other. A completed rebalance round establishes another delay before further optional moves. Balanced rounds preserve every placement.

This policy balances queue counts. Queue traffic and processing costs determine actual workload, so partition sizing remains an important capacity decision. Missing-agent activation and recovery proceed immediately, while optional redistribution waits for stabilization.

A keep-alive grain timer sustains active polling, and each timer invocation performs at most 16 queue reads so a busy partition regularly reaches the runtime's migration boundary.

### Grain-hosted handoff

The successful migration path orders these operations:

1. The source activation stops admitting queue work and cancels outstanding grain-hosted subscription and delivery waits.
1. Local asynchronous work completes. The final watermark includes acknowledged progress; interrupted registration preserves the prior checkpoint.
1. Receiver shutdown flushes the safe checkpoint and releases activation-local resources.
1. Accepted publisher registrations reach their actual RPC completion, including registrations whose local wait was canceled.
1. The runtime transfers the activation and conditionally registers its successor.
1. The destination initializes its receiver and cache, loads the durable checkpoint, and starts polling.

Receiver, cache, cursor, and SDK-client objects belong to their host. The destination reconstructs subscriptions from pub/sub and consumer handshakes. Event Hubs retains its existing checkpoint storage identity and inclusive restart boundary.

The producer's stable grain ID remains registered across runtime migration and recovery. Pub/sub callbacks route to the successor. During graceful silo deactivation, the grain flushes its receiver and requests migration to an eligible surviving host. The coordinator maintains queue coverage on other running provider hosts.

### Publisher lifetime and retirement

Explicit pub/sub registrations belong to the stable provider/queue grain identity. Before registering a stream, that grain records the qualified stream ID in its write-ahead publisher index. The index uses the named provider's grain storage, with the same `PubSubStore` fallback as rendezvous grains. Each activation reads this state before creating its receiver. Implicit-only providers resolve subscriptions from metadata and use no publisher index.

An activation-local cache eviction releases cursors and consumer state. The persistent index retains the stream ID, so a subsequent activation can retire every publisher registered by earlier activations. Registration admission covers both the storage write and the actual pub/sub RPC; canceling the engine's wait leaves that operation tracked until completion. Migration drains these operations before the successor initializes.

Administrative stop closes local admission and visits the provider's configured queue catalog sequentially. Calls route through the grain directory to the current activation. The stop request includes the addressed silo, and an activation on another host preserves its receiver and registrations. A dormant queue can activate for retirement on the explicitly stopped requesting host; its receiver stays stopped. This allows a provider-wide stop, applied to each host, to cover dormant queues as well as live ones.

After receiver and registration drains succeed, retirement unregisters each indexed publisher and persists removal of that index entry. A failure is logged and returned to the operator. The remaining durable entries support another stop attempt. Ambiguous index writes trigger a state reread before subsequent mutation, so a retry works from the storage provider's committed state.

Each newly encountered stream adds one index write, and each retired stream adds one removal write. Repeated registration of an indexed stream reuses its durable intent. Index size grows with the number and encoded size of distinct stream IDs seen since the last successful administrative retirement, including inactive streams. Size partitions and stop intervals against the selected storage provider's record-size and throughput limits. The index is a versioned serialized state record scoped to the existing queue grain identity.

Receiver initialization and final-flush failures propagate to the grain lifecycle. An agent retains a failed drain outcome for its deactivation callback; a successful receiver restart establishes a fresh lifecycle. Each adapter supplies its acknowledgement and recovery semantics, and the selected grain directory supplies activation-registration and failure-recovery guarantees. Qualifying process-failure recovery requires a hard-process-termination test of that exact adapter, checkpoint store, pub/sub store, and directory combination. In-process migration and simulated receiver failure establish graceful lifecycle ordering; production crash qualification remains a deployment prerequisite.

Queue-balancer configuration, including lease-based queue balancing, applies to system-target hosting. See [streaming operations](../../streaming/streaming-operations.md#change-pulling-agent-hosting-mode) for the provider-scoped rollout boundary and the scope of administrative commands.

Source: [`PersistentStreamPullingManager`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Streaming/PersistentStreams/PersistentStreamPullingManager.cs).

## Pulling-agent loop <a name="pulling-agents"></a>

<a name="pulling-protocol"></a>

`PullingAgentTarget` and `PullingAgentGrain` use the same `PersistentStreamPullingAgent` processing implementation with single-threaded Orleans scheduling. Its loop:

1. asks the adapter receiver for a batch;
1. adds batch containers to its queue cache;
1. groups cached items by stream;
1. resolves and caches pub-sub registrations;
1. advances each subscription's cursor independently;
1. sends events through Orleans messaging;
1. records delivery progress and failure; and
1. purges only data which the cache says is safe to remove.

The default maximum adapter batch-container batch size is 1 and the empty-poll period is 100 ms. These defaults are runtime behavior, not a universal throughput recommendation.

Each initialization creates a fresh `AdmissionGate`. Scoped admissions cover queue pumping, stream registration, subscriber handshakes and attachment, and cursor delivery. System-target shutdown closes admission and drains actual accepted operations before updating final delivery progress and releasing queue resources. Grain hosting creates an additional cancellation scope for interruptible subscription and delivery waits; its publisher registry separately drains actual registration completions. Cancellation preserves acknowledged delivery and drained-subscription progress, while unresolved handshakes withhold checkpoint advancement. Reinitialization awaits the full previous shutdown before opening the next run's admission.

## Cache and cursor invariants <a name="queue-cache"></a>

<a name="backpressure"></a>

An <xref:Orleans.Streams.IQueueCache> decouples queue reads from consumer delivery. Each subscription has an <xref:Orleans.Streams.IQueueCacheCursor>, so a slow consumer does not directly block a fast consumer at a later cursor.

The cache tracks the earliest delivery progress across active subscriptions. Purging must not remove an item still needed by any cursor. <xref:Orleans.Providers.Streams.Common.SimpleQueueCache> uses pressure buckets to stop or slow reads as lag grows instead of discarding undelivered events. Its default capacity is 4,096 batch containers.

```mermaid
flowchart TB
    New[New queue batches] --> Cache[Queue cache]
    Cache --> C1[Cursor A: fast]
    Cache --> C2[Cursor B: slow]
    C1 --> P1[Consumer A]
    C2 --> P2[Consumer B]
    C1 --> Progress[Earliest safe progress]
    C2 --> Progress
    Progress --> Purge[Purge or apply backpressure]
```

Cache capacity is not durability. The queue remains the durable boundary, subject to the adapter's acknowledgement contract.

## Pub-sub handshake

The agent registers as a producer for each stream and obtains subscription records from stream pub-sub. It holds a pin cursor while subscription handshakes complete so cache cleanup cannot pass the requested start token. New subscription notifications update the agent's local pub-sub cache.

Sequence tokens allow a rewindable adapter to start from a supported historical position. An adapter whose <xref:Orleans.Streams.IQueueAdapter.IsRewindable?displayProperty=nameWithType> property is `false` must reject unsupported tokens rather than pretending to honor them.

## Delivery and failure semantics

The agent normally awaits delivery before advancing a subscription cursor, creating per-subscription backpressure. When delivery fails, it invokes the configured <xref:Orleans.Streams.IStreamFailureHandler>. Depending on provider policy, an explicit subscription can be faulted and removed.

Persistent streams are not universally exactly once. Semantics depend on:

- when the external queue considers a message acknowledged;
- whether the adapter can redeliver after receiver or silo failure;
- cache checkpoint behavior;
- consumer idempotency; and
- provider-specific sequence tokens.

A queue message can be delivered again after ownership change or failure. Consumers which perform durable side effects should be idempotent.

## Extension contracts

Provider authors should keep these responsibilities separate:

- <xref:Orleans.Streams.IQueueAdapter> defines external queue reads/writes and rewindability.
- <xref:Orleans.Streams.IQueueAdapterReceiver> defines receive, acknowledgement, and shutdown.
- <xref:Orleans.Streams.IStreamQueueMapper> defines stable partition mapping.
- <xref:Orleans.Streams.IStreamQueueBalancer> supplies queue assignments for system-target hosting.
- <xref:Orleans.Streams.IQueueCache> and its cursors define buffering and safe purge.
- <xref:Orleans.Streams.IStreamFailureHandler> defines delivery failure policy.

See [provider authoring](../provider-authoring.md) for hosting and validation patterns and [Azure Queue streams](azure-queue-streams.md) for a concrete adapter.
