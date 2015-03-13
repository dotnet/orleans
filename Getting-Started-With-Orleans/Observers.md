---
layout: page
title: Observers
---
{% include JB/setup %}

Because the standard .NET event facility is explicitly synchronous, it doesn’t fit into an asynchronous framework such as Orleans. Instead, Orleans uses the Observer pattern.

A grain type that allows observation will define an observer interface that inherits from the `IGrainObserver` interface. Methods on an observer interface correspond to events that the observed grain type makes available. An observer would implement this interface and then subscribe to notifications from a particular grain. The observed grain would call back to the observer through the observer interface methods when an event has occurred.

Methods on observer interfaces must be `void` since event messages are one-way. If the observer needs to interact with the observed grain as a result of a notification, it must do so by invoking normal methods on the observed grain.

The observed grain type must expose a method to allow observers to subscribe to event notifications from a grain. In addition, it is usually convenient to expose a method that allows an existing subscription to be canceled. Grain developers may use the Orleans `ObserverSubscriptionManager<T>` generic class to simplify development of observed grain types.