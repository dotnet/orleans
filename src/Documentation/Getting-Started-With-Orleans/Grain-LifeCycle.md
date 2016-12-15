---
layout: page
title: Grain LifeCycle
---


## Grain LifeCycle

"Grains are logical entities that always exist, virtually, and have stable logical identities (keys). Application code never creates or destroys grains. Instead, it acts as if all possible grains are always in memory and available for processing requests.

Grains get physically instantiated, activated, by the Orleans runtime automatically on an as-needed to process incoming requests. After a grain has been idle for a certain amount of time, the Orleans runtime automatically removes, deactivates, it from memory.

A physical instance of a grain in memory is called a grain activation. Grain activations are invisible to application code as well as the process of activating and deactivating them. Only the grain itself can be aware of that - by overriding virtual methods  OnActivateAsync  and  OnDeactivateAsync  that get invoked upon activation and deactivation of the grain respectively.

Over the course of its eternal, virtual, life a grain goes through the cycles of activations and deactivations, staying always available for callers to invoke it, whether it is in memory at the time of the call or not."

When you get a grain reference in a client or in another grain by using `GetGrain`,
you only get a proxy object with a logical address (identity) of the grain, but not its physical address.
When you call a method using the proxy object, then the grain will get activated in a silo (if it is not already activated in the cluster).
Your method calls on the proxy are sent to the activation by the Orleans runtime.

`GetGrain`  itself is an inexpensive local operation of constructing a proxy object with an embedded identity of the target grain.

## callbacks 

Just like the class which can have constructors and (destructors in some languages), grains have
`OnActivateAsync` and `OnDeactivateAsync` virtual methods which you can override.

## Different grain types and their differences regarding life cycle

Your grains can be persistent and store their state in storage
or have no state which is stored in storage. 
In case of the later, all activations are created equal and there is no difference between them but in case of the former
each activation reads the state from storage so the `State` contains latest stored state.
For grains which store their state and derive from `Grain<T>` there is a point which you should consider.

> `ReadStateAsync` is called before calling `OnActivateAsync` and if it fails then the grain will not be activated.


## Sequence of events in a grain's life cycle

So the life cycle of a grain is like this

- another grain or a client calls a method of a grain
- the grain gets activated (if it is not activated anywhere in the silo) and an instance of the grain class will be created
  - Constructor of the grain will be executed and DI will setup (If you have DI)
  - the grain state will be read from storage if any
  - if successful then `OnActivateAsync` is called
- The grain will process some messages
- The grain remains idle for some time
- Runtime will call `OnDeactivateAsync`
- Runtime removes the grain from memory

Now if a Silo gets a shutdown request gracefully, the message queues of all activated grains will be forwarded to another silo with the grain activation,
but if a silo crashes, `OnDeactivateAsync` will not be called so you can not rely on it to store state for fault tolerance unless you can live with losing some state.

## Next
Next we look at how easy it is to debug our Orleans application

[Debugging](Debugging.md)
