---
title: Choose an Orleans messaging abstraction
description: Choose among grain calls, response streaming, observers, Orleans streams, and broadcast channels.
ms.date: 08/08/2026
ms.topic: concept-article
---

# Choose an Orleans messaging abstraction

Start from the relationship between sender and receiver and from the failure behavior your application needs.

| Use | Best fit | Important behavior |
|---|---|---|
| Invoke a known grain and await a result | **Grain call** | Addressed request/response with Orleans call semantics. The caller knows the target grain identity. |
| Return one grain call's results progressively | **Response streaming (`IAsyncEnumerable<T>`)** | Pull-based, single-caller enumeration. It isn't multicast, retained, or durable. |
| Push transient notifications from grains to a connected client | **Grain observer** | Ephemeral client callback. The application registers and removes observer references and handles disconnects. |
| Publish typed events to multiple independent subscriptions | **Orleans stream** | Multicast pub/sub. Provider selection controls durability, retries, ordering, and replay. Explicit subscriptions can survive activation changes. |
| Send best-effort notifications to grains selected from a channel identity | **Broadcast channel** | Implicit, nonpersistent fan-out. No queue, history, replay, or durable subscription registry. |

## Prefer grain calls for commands and queries

Use a grain call when the sender knows which grain owns the operation, needs a return value, or needs failure to propagate through the call. Grain calls make ownership and control flow explicit. Don't introduce a stream merely to avoid calling a known grain.

Use **response streaming** when one call produces many results: the grain method returns <xref:System.Collections.Generic.IAsyncEnumerable`1> so the caller can process the response incrementally. See [Response streaming with IAsyncEnumerable](../grains/response-streaming.md).

## Prefer observers for client callbacks

Use a grain observer when a connected Orleans client wants transient callbacks and can re-register after reconnecting. See [Orleans observers](../grains/observers.md) for the supported lifecycle and registration model. Observers are not categorically redundant: they are a low-overhead direct-callback API for a small set of live client objects, while streams are the appropriate abstraction for multicast event flow and provider-managed delivery guarantees.

Choose streams instead when a producer needs to fan out to many independent subscribers, when delivery must survive client disconnects or grain reactivation, or when a provider offers replay, retention, or durable subscription state. Observer references are not durable subscriptions and are not a replacement for retained events or server-side pub/sub.

## Prefer streams for multicast event flow

Streams decouple producers from consumers in identity, time, and placement. They fit per-entity event feeds, dynamic subscriptions, integration with external brokers, and stateful event processing in grains.

Orleans streams are multicast, not point-to-point work queues: every subscription to a logical stream receives the item. A provider can partition physical queues for scale, but that doesn't change the logical fan-out model. If only one worker must claim each job, model that ownership explicitly instead of assuming competing-consumer semantics.

The provider is part of the design. A durable queue can retain accepted events across process failure; a rewindable provider can start a subscription from an earlier token; neither capability is implied by the Orleans stream API itself.

## Why Orleans streams are different

Orleans streams complement event brokers and data-flow engines rather than replacing them. Brokers retain and transport events, while data-flow engines excel at applying a shared query or transformation pipeline to large event sets. Orleans streams are useful when each entity needs independently addressed, stateful processing expressed in ordinary grain code.

### Flexible processing logic

<a id="flexible-stream-processing-logic"></a>

A stream consumer is application code. It can update grain state, make grain calls, publish to other streams, call external services, or choose behavior from the grain's current state. Processing can be imperative or functional, stateful or stateless, and can include side effects.

Orleans streams don't provide a declarative query language or automatically compile a data-flow graph. Use a dedicated stream-processing engine when windowing, joins, aggregations, or a centrally managed query topology are the primary requirement. Use Orleans streams when events must enter fine-grained actor workflows with per-entity state and behavior.

### Dynamic, fine-grained topologies

<a id="support-for-dynamic-topologies"></a>
<a id="fine-grained-stream-granularity"></a>

The processing topology emerges from stream subscriptions and grain logic instead of being one deployment-wide graph. Applications can add or remove explicit subscriptions at runtime, use implicit subscriptions to activate grains from stream identities, and change how an individual grain responds as its state changes.

Streams are independently addressed by provider, namespace, and key. A grain can consume or produce multiple streams, and an application can use different providers for different links according to their durability, replay, throughput, and operational requirements. This granularity supports per-user, per-device, per-tenant, and similar event flows without deploying a separate pipeline for every entity.

### Distributed execution

Stream consumers are grains, so their processing is distributed using the Orleans runtime. The application can scale the cluster, distribute stream identities across grains, recover grain activations after failures, and combine stream processing with Orleans placement, persistence, and messaging.

These properties don't remove the need to design for provider-specific delivery guarantees, ordering, replay, backpressure, and hot keys. See [Delivery, ordering, replay, and recovery](delivery-semantics.md) and [Operate and tune Orleans streams](streaming-operations.md).

## Prefer broadcast channels for transient fan-out

Broadcast channels are useful for cache invalidation hints, live configuration notifications, and similar signals where occasional loss is acceptable. A channel key maps to a subscriber grain identity for every matching subscriber grain type; it doesn't broadcast to every activation in the cluster.

Use a stream instead when consumers need durable subscription records, retained events, retries after consumer failure, or replay. See [Broadcast channels](broadcast-channel.md) for the complete identity and delivery model.
