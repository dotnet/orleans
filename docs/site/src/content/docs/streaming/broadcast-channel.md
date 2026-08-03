---
title: Broadcast channels
description: Configure and use Orleans broadcast channels with correct identity and delivery semantics.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Broadcast channels

Broadcast channels provide nonpersistent, implicit fan-out to grains. They don't use a queue, retain history, retry from a log, or maintain explicit subscriptions. Use them for best-effort notifications where loss is acceptable; use [Orleans streams](index.md) when events or subscriptions need durability or replay.

## Identity and routing

A broadcast writer is selected by a configured **provider name** and a <xref:Orleans.BroadcastChannel.ChannelId>. `ChannelId.Create(namespace, key)` has two distinct routing roles:

- The **namespace** is matched against <xref:Orleans.ImplicitChannelSubscriptionAttribute> declarations to select subscriber grain types.
- The **key** maps to the primary key of one subscriber grain identity for each matching grain type.

Publishing doesn't enumerate all current activations. It addresses the matching virtual grain identities, activating them when necessary. The default mapper interprets the channel key for each matching grain type: it uses raw text for a string-keyed grain, parses GUID text for a GUID-keyed grain, and parses decimal text for an integer-keyed grain. `ChannelId.Create(namespace, guid)` formats a GUID key; use a decimal string for an integer key. Custom <xref:Orleans.BroadcastChannel.IChannelIdMapper> implementations can change that mapping.

The provider name and channel namespace are independent. They can use the same string by convention, but provider registration doesn't make that string the channel namespace.

## Configure silos and clients

Register a named broadcast provider on every silo:

:::code language="csharp" source="./snippets/broadcastchannel/BroadcastChannel.Silo/Program.cs":::

An Orleans client that publishes must register the same provider name and compatible options:

:::code language="csharp" source="./snippets/broadcastchannel/BroadcastChannel.Client/Program.cs":::

`BroadcastChannelOptions.FireAndForgetDelivery` defaults to `true`. In that mode, `Publish` starts subscriber calls and returns without awaiting them; subscriber exceptions are logged and aren't returned to the publisher. Setting it to `false` awaits all subscriber callbacks and propagates failures as an aggregate exception. Neither mode adds persistence, retry, replay, or exactly-once processing.

## Define a subscriber grain

Mark the grain class with an implicit channel subscription and implement <xref:Orleans.BroadcastChannel.IOnBroadcastChannelSubscribed>. Attach a callback when Orleans supplies the channel subscription:

:::code language="csharp" source="./snippets/broadcastchannel/BroadcastChannel.Silo/LiveStockGrain.cs":::

The parameterless attribute matches all nonempty channel namespaces. Pass a namespace to match exactly, use <xref:Orleans.RegexImplicitChannelSubscriptionAttribute> for a pattern, or provide a custom namespace predicate.

`Attach<T>` selects the payload type and supplies item and error callbacks. Channel subscriptions are implicit metadata bindings, so there is no `SubscribeAsync`, subscription handle, or `UnsubscribeAsync`.

## Publish

Resolve <xref:Orleans.BroadcastChannel.IBroadcastChannelProvider> by provider name, construct a channel ID, get a typed writer, and publish:

:::code language="csharp" source="./snippets/broadcastchannel/BroadcastChannel.Silo/Services/StockWorker.cs":::

In this sample, the channel namespace is `live-stock-ticker` and the key is `Guid.Empty`, so each matching GUID-keyed subscriber grain type receives the message at its `Guid.Empty` grain identity. Use a customer, tenant, device, or other domain key to target the corresponding identity instead.

## Broadcast channels compared with streams

| Capability | Broadcast channel | Orleans stream |
|---|---|---|
| Fan-out | One grain identity per matching subscriber grain type | Every explicit and implicit subscription |
| Subscription model | Implicit grain metadata only | Explicit and implicit |
| Event persistence | None | Provider-dependent |
| Subscription persistence | None required | Explicit records depend on `PubSubStore` |
| Replay | No | Provider-dependent |
| Publisher completion | Fire-and-forget by default; optionally awaits callbacks | Provider acceptance, not general consumer completion |
| External broker integration | No | Available through stream providers |

See [Choose an Orleans messaging abstraction](streams-why.md) for selection guidance.
