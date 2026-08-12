---
title: Orleans observers
description: Send asynchronous notifications from grains to clients or other grains.
ms.date: 08/07/2026
ms.topic: concept-article
---

# Orleans observers

Observers let grains call an object hosted by an Orleans client or another grain. They are useful for live, best-effort notifications while the receiver is connected.

Observers aren't durable subscriptions. A client can disconnect without notice, and a recreated observer has a different identity. Use [Orleans streams](../streaming/index.md) or another durable messaging mechanism when subscriptions or delivery must survive failures.

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
