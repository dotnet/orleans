---
title: Broadcast channels
description: Learn how to work with Orleans broadcast channels.
ms.date: 07/03/2024
ai-usage: ai-assisted
---

# Broadcast channels in Orleans

Broadcast channels are a special type of broadcasting mechanism that can be used to send messages to all subscribers. Unlike [streaming providers](stream-providers.md), broadcast channels are not persistent and don't store messages, and they're not a replacement for persistent streams. With broadcast channels, grains are implicitly subscribed to the broadcast channel and receive broadcast messages from a producer. This decouples the sender and receiver of the message, which is useful for scenarios where the sender and receiver are not known in advance.

To use broadcast channel you should configure Orleans Streams and then enable broadcast on your channel using the [`AddBroadcastChannel`](/dotnet/api/orleans.hosting.channelhostingextensions.addbroadcastchannel) during silo configuration.

```csharp
siloBuilder.AddBroadcastChannel(OrleansBroadcastChannelNames.ReadmodelChanges);
```

## Example scenario

Consider a scenario where you have a grain that needs to receive stock price updates from a stock price provider. The stock price provider is a background service that publishes stock price updates to a broadcast channel. Grains are implicitly subscribed to the broadcast channel and receive updated stock prices. The following diagram shows the scenario:

:::image type="content" source="snippets/media/broadcastchannel-diagram.png" lightbox="snippets/media/broadcastchannel-diagram.png" alt-text="Stock prices diagram depicting a silo, a stock grain and consuming client in a simple broadcast channel architecture.":::

In the preceding diagram:

- The silo publishes stock price updates to the broadcast channel.
- The grain subscribes to the broadcast channel and receives stock price updates.
- The client consumes the stock price updates from the stock grain.

The broadcast channel decouples the producer and consumer of the stock price updates. The producer publishes stock price updates to the broadcast channel, and the consumer subscribes to the broadcast channel and receives stock price updates.

## Define a consumer grain

To consume broadcast channel messages, your grain needs to implement the <xref:Orleans.BroadcastChannel.IOnBroadcastChannelSubscribed> interface. This interface enables implicit subscriptions, meaning grains are automatically subscribed to the broadcast channel when they're activated. Your implementation uses the <xref:Orleans.BroadcastChannel.IBroadcastChannelSubscription.Attach*?displayProperty=nameWithType> method to attach to the broadcast channel. The `Attach` method takes a generic-type parameter for the message type you're going to receive.

First, define the grain interface that consumers use to interact with the grain:

:::code source="./snippets/broadcastchannel/BroadcastChannel.GrainInterfaces/ILiveStockGrain.cs":::

The `ILiveStockGrain` interface uses `IGrainWithGuidKey`, which means the grain is identified by a GUID key. Next, implement the grain that subscribes to the broadcast channel:

:::code source="./snippets/broadcastchannel/BroadcastChannel.Silo/LiveStockGrain.cs":::

In the preceding code:

- The `LiveStockGrain` grain implements the `IOnBroadcastChannelSubscribed` interface.
- The `[ImplicitChannelSubscription]` attribute marks this grain for automatic subscription to broadcast channels.
- The `OnSubscribed` method is called automatically when the grain is activated (when it's first used or after recovery from a failure).
- The `subscription` parameter is used to call the `Attach` method to attach to the broadcast channel.
  - The `OnStockUpdated` method is passed to `Attach` as a callback that fires when the `Stock` message is received.
  - The `OnError` method is passed to `Attach` as a callback that fires when an error occurs.

This example grain contains the latest stock prices as published on the broadcast channel. Any client that asks this grain for the latest stock price gets the latest price from the broadcast channel.

## Publish messages to a broadcast channel

To publish messages to the broadcast channel, you need to get a reference to the broadcast channel. To do this, get the <xref:Orleans.BroadcastChannel.IBroadcastChannelProvider> from the <xref:Orleans.IClusterClient>. With the provider, call the <xref:Orleans.BroadcastChannel.IBroadcastChannelProvider.GetChannelWriter*?displayProperty=nameWithType> method to get an instance of <xref:Orleans.BroadcastChannel.IBroadcastChannelWriter`1>. The writer is used to publish messages to the broadcast channel.

First, define a constant for the channel name to ensure the producer and consumers use the same channel identifier:

:::code source="./snippets/broadcastchannel/BroadcastChannel.GrainInterfaces/ChannelNames.cs":::

Then, create a publisher that sends messages to the broadcast channel:

:::code source="./snippets/broadcastchannel/BroadcastChannel.Silo/Services/StockWorker.cs":::

In the preceding code:

- The `StockWorker` class is a background service that publishes messages to the broadcast channel.
- The constructor takes a `StockClient` and <xref:Orleans.IClusterClient> as parameters.
- From the cluster client instance, the <xref:Orleans.Hosting.ChannelHostingExtensions.GetBroadcastChannelProvider*> method is used to get the broadcast channel provider for the `LiveStockTicker` channel.
- The `ChannelId.Create` method creates a channel identifier using:
  - The channel name (`ChannelNames.LiveStockTicker`)—this must match the name used when configuring the broadcast channel in the silo setup.
  - `Guid.Empty` as the namespace—for broadcast channels, all subscribers receive all messages, so the namespace is typically set to `Guid.Empty` to indicate a single shared broadcast.
- Using the `StockClient`, the `StockWorker` class gets the latest stock price for each stock symbol.
- Every 15 seconds, the `StockWorker` class publishes `Stock` messages to the broadcast channel.

The publishing of messages to a broadcast channel is decoupled from the consumer grain. The producer doesn't know about specific consumer grains. Instead, it publishes to the broadcast channel, and all implicitly subscribed grains automatically receive the messages.

## Broadcast channels vs. streams

Broadcast channels and Orleans streams (including in-memory streams) are both messaging mechanisms, but they serve different purposes and have different characteristics. The following table compares the key differences:

| Feature | Broadcast channels | Orleans streams |
|---------|-------------------|-----------------|
| **Subscription model** | Implicit—grains are automatically subscribed when activated | Explicit—grains must explicitly subscribe to streams |
| **Message persistence** | Not persistent—messages are lost if no subscribers are active | Can be persistent (Azure Queues, Event Hubs) or transient (in-memory) |
| **Message delivery** | Best-effort, fire-and-forget delivery | Depends on provider—can support at-least-once or exactly-once delivery |
| **Use case** | Broadcasting the same message to all interested grains in real-time | Point-to-point or pub-sub messaging with delivery guarantees |
| **Message history** | No message history—only current broadcasts | Streams can support rewindable subscriptions with message history |
| **Scalability** | Optimized for fan-out to many consumers | Optimized for queue-based processing with backpressure |
| **Consumer lifecycle** | Consumers are implicitly managed by Orleans | Consumers must manage subscription lifecycle |
| **Configuration** | Simple—requires only channel name | More complex—requires stream provider configuration |

### When to use broadcast channels

Use broadcast channels when:

- You need to send the same message to all instances of a grain type.
- Message delivery isn't critical (occasional losses are acceptable).
- You want implicit subscription without managing subscription lifecycle.
- You need real-time updates without message history.
- You want simple configuration and setup.

### When to use streams

Use streams when:

- You need guaranteed message delivery.
- You need message persistence and replay capabilities.
- You want explicit control over subscription lifecycle.
- You need backpressure and flow control mechanisms.
- Your messaging pattern is point-to-point or requires more complex routing.
- You're integrating with external queuing systems (Event Hubs, Service Bus, Kafka).

## See also

- [Streaming with Orleans](index.md)
- [Orleans stream providers](stream-providers.md)
