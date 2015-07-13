---
layout: page
title: Client Observers
---
{% include JB/setup %}

There are situations in which a simple message/response pattern is not enough, and the client needs to receive asynchronous notifications. 
For example, a user might want to be notified when a new instant message has been published by a friend.

Client observers is a mechanism that allows notifying clients asynchronously. 
An observer is a one-way asynchronous interface that inherits from `IGrainObserver`, and all its methods must be void. 
The grain sends a notification to the observer by invoking it like a grain interface method, except that it has no return value, and so the grain need not depend on the result. 
The Orleans runtime will ensure one-way delivery of the notifications. 
A grain that publishes such notifications should provide an API to add or remove observers. 
In addition, it is usually convenient to expose a method that allows an existing subscription to be cancelled. 
Grain developers may use the Orleans `ObserverSubscriptionManager<T>` generic class to simplify development of observed grain types.

To subscribe to a notification, the client must first create a local C# object that implements the observer interface. 
It then calls a static method on the observer factory, `CreateObjectReference()`, to turn the C# object into a grain reference, which can then be passed to the subscription method on the notifying grain.

This model can also be used by other grains to receive asynchronous notifications. 
Unlike in the client subscription case, the subscribing grain simply implements the observer interface as a facet, and passes in a reference to itself (e.g. `MyGrainFactory.Cast(this)`).

##Next

Next we look at [Developing a Grain](Developing-a-Grain) 
