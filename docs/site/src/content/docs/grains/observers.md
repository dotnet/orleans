---
title: Orleans observers
description: Send asynchronous notifications from grains to clients or other grains.
ms.date: 08/07/2026
ms.topic: concept-article
---

# Orleans observers

Observers let grains call an object hosted by an Orleans client or another grain. They are useful for live, best-effort notifications while the receiver is connected.

Use observers for a small set of known, live callbacks. They are intentionally transient and are not a general replacement for a durable event bus. A client can disconnect without notice, and a recreated observer has a different identity. Use [Orleans streams](../streaming/index.md) or another durable messaging mechanism when subscriptions or delivery must survive failures or fan out to many independent consumers.

## When to choose observers versus streams

Use a grain observer when the callback target is a specific connected client object or grain and the application only needs a live notification while that connection remains active. Observers carry very little infrastructure: a callback target is registered, and the grain notifies that target directly.

Use an Orleans stream when the application needs multicast delivery, dynamic subscriptions, playback, provider-defined durability, or recovery after a client or grain restarts. A stream can outlive a single activation or connection, while an observer registration is tied to the callback target and must be re-established after reconnect.

The tradeoff is mostly about lifetime and delivery guarantees:

- Observers are low-overhead, direct callbacks. They are best for ephemeral status or UI updates, but they are not durable, replayable, or automatically recovered after disconnects.
- Streams are more operationally expensive because they depend on a configured provider, subscription records, and provider-specific delivery semantics. In return, they support independent subscribers, retained events, and more flexible failure recovery.

See [Choose an Orleans messaging abstraction](../streaming/streams-why.md) and [Orleans streaming APIs](../streaming/streams-programming-apis.md) for the broader decision guidance.

## Define an observer

Observer interfaces derive from <xref:Orleans.IGrainObserver>:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="chat_observer":::
Use asynchronous return types. Avoid `async void`. Apply <xref:Orleans.Concurrency.OneWayAttribute> only when notifications are deliberately best effort and the publisher doesn't need exceptions or completion.

## Create and remove a client observer reference

Convert the local object into an addressable reference:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="subscribe_observer":::
Keep a strong reference to the local observer for as long as it should receive calls. When finished, unsubscribe and delete the object reference:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="unsubscribe_observer":::
Deleting the reference releases the client-side registration. Failing to delete long-lived registrations can leak resources.

## Manage subscriptions in a grain

<xref:Orleans.Utilities.ObserverManager`1> tracks observers, expires stale entries, and removes observers whose notifications fail:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="chat_room_observer_manager":::
The current API is <xref:Orleans.Utilities.ObserverManager`2.Notify*>, including the overload that accepts <xref:System.Func`2> returning a <xref:System.Threading.Tasks.Task>. There is no `NotifyAsync` method.

Subscriptions expire lazily after <xref:Orleans.Utilities.ObserverManager`2.ExpirationDuration>. Clients should renew before expiry. A notification exception causes <xref:Orleans.Utilities.ObserverManager`1> to remove that observer; it doesn't fail the entire publish operation.

Use <xref:Orleans.Utilities.ObserverManager`2> when the subscription identity should differ from the observer reference.

## Grain observers

A grain can implement an observer interface and pass a reference to itself:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="subscribe_grain_observer":::
Don't call <xref:Orleans.IGrainFactory.CreateObjectReference*> for a grain. Grains are already addressable.

## Execution and cancellation

Calls to one client observer reference execute sequentially and aren't reentrant. Different observer references can execute concurrently.

Observer methods can accept a <xref:System.Threading.CancellationToken> parameter. Cancellation remains cooperative and doesn't make observer delivery durable. See [Cancel Orleans grain calls](cancellation-tokens.md) for cancellation semantics.
