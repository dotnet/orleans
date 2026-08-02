---
title: Choose an Orleans messaging abstraction
description: Choose between grain calls, grain observers, Orleans streams, and broadcast channels.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Choose an Orleans messaging abstraction

Start from the relationship between sender and receiver and from the failure behavior your application needs.

| Use | Best fit | Important behavior |
|---|---|---|
| Invoke a known grain and await a result | **Grain call** | Addressed request/response with Orleans call semantics. The caller knows the target grain identity. |
| Push transient notifications from grains to a connected client | **Grain observer** | Ephemeral client callback. The application registers and removes observer references and handles disconnects. |
| Publish typed events to multiple independent subscriptions | **Orleans stream** | Multicast pub/sub. Provider selection controls durability, retries, ordering, and replay. Explicit subscriptions can survive activation changes. |
| Send best-effort notifications to grains selected from a channel identity | **Broadcast channel** | Implicit, nonpersistent fan-out. No queue, history, replay, or durable subscription registry. |

## Prefer grain calls for commands and queries

Use a grain call when the sender knows which grain owns the operation, needs a return value, or needs failure to propagate through the call. Grain calls make ownership and control flow explicit. Don't introduce a stream merely to avoid calling a known grain.

## Prefer observers for client callbacks

Use a grain observer when a connected Orleans client wants transient callbacks and can re-register after reconnecting. Observer references aren't durable subscriptions. They aren't a replacement for retained events or server-side pub/sub.

## Prefer streams for multicast event flow

Streams decouple producers from consumers in identity, time, and placement. They fit per-entity event feeds, dynamic subscriptions, integration with external brokers, and stateful event processing in grains.

Orleans streams are multicast, not point-to-point work queues: every subscription to a logical stream receives the item. A provider can partition physical queues for scale, but that doesn't change the logical fan-out model. If only one worker must claim each job, model that ownership explicitly instead of assuming competing-consumer semantics.

The provider is part of the design. A durable queue can retain accepted events across process failure; a rewindable provider can start a subscription from an earlier token; neither capability is implied by the Orleans stream API itself.

## Prefer broadcast channels for transient fan-out

Broadcast channels are useful for cache invalidation hints, live configuration notifications, and similar signals where occasional loss is acceptable. A channel key maps to a subscriber grain identity for every matching subscriber grain type; it doesn't broadcast to every activation in the cluster.

Use a stream instead when consumers need durable subscription records, retained events, retries after consumer failure, or replay. See [Broadcast channels](broadcast-channel.md) for the complete identity and delivery model.
