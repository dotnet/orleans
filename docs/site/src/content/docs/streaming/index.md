---
title: Streaming with Orleans
description: Understand Orleans streams and choose the right messaging abstraction for an Orleans 10 application.
ms.date: 08/02/2026
ms.topic: overview
---

# Streaming with Orleans

Orleans streams are typed, logical, multicast channels. Producers and consumers address a stream by provider name and <xref:Orleans.Runtime.StreamId>; they don't need references to one another. A stream can have many producers and many subscriptions, and every published item is offered to every subscription on that stream.

The stream is a virtual address. Its transport, retention, retry behavior, ordering, and ability to replay are supplied by the configured [stream provider](stream-providers.md). A stream handle alone doesn't make data durable.

Use streams when events should fan out to independently managed consumers, when subscriptions should outlive a grain activation, or when an external queue or event log should feed grains. For request/response calls, transient client callbacks, or low-overhead best-effort fan-out, another Orleans messaging abstraction can be a better fit. Start with [Choose an Orleans messaging abstraction](streams-why.md).

## Programming model

- A <xref:Orleans.Runtime.StreamId> consists of a namespace and a key. The provider name selects the configured transport.
- <xref:Orleans.Streams.IAsyncStream`1> is both a producer and consumer handle. Getting a handle is a local operation; publishing and subscribing perform work.
- Streams are multicast. Multiple producers can publish to one stream, and each active subscription receives each item.
- Subscriptions can be [explicit or implicit](streams-programming-apis.md#explicit-and-implicit-subscriptions).
- Delivery guarantees, ordering, and replay are [provider-specific](delivery-semantics.md).
- Explicit subscription records can be durable only when `PubSubStore` uses [durable grain storage](pubsub-storage.md).

## Get started

1. Follow the [streaming quickstart](streams-quick-start.md) with the in-memory provider.
1. Learn stream [identity, production, consumption, and subscription APIs](streams-programming-apis.md).
1. Choose a [provider](stream-providers.md) for the required durability and replay behavior.
1. Plan for [delivery, ordering, replay, and recovery](delivery-semantics.md).
1. Configure [PubSub storage](pubsub-storage.md) and [operations](streaming-operations.md) for production.

For best-effort implicit fan-out without queueing or replay, see [broadcast channels](broadcast-channel.md). For the runtime architecture behind persistent streams, see [Orleans streams implementation](../implementation/streams-implementation/index.md).
