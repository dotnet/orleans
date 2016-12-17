---
layout: page
title: Grain Lifecycle
---


## Grain Lifecycle

Grains are logical entities that always exist, virtually, and have stable logical identities (keys). Application code never creates or destroys grains. Instead, it acts as if all possible grains are always in memory and available for processing requests.

Grains get physically instantiated, activated, by the Orleans runtime automatically on an as-needed to process incoming requests. After a grain has been idle for a certain amount of time, the Orleans runtime automatically removes, deactivates, it from memory.

A physical instance of a grain in memory is called a grain activation. Grain activations are invisible to application code as well as the process of activating and deactivating them. Only the grain itself can be aware of that - by overriding virtual methods  OnActivateAsync  and  OnDeactivateAsync  that get invoked upon activation and deactivation of the grain respectively.

Over the course of its eternal, virtual, life a grain goes through the cycles of activations and deactivations, staying always available for callers to invoke it, whether it is in memory at the time of the call or not.

When you get a grain reference in a client or in another grain by using `GetGrain`, you only get a proxy object, called a grain reference, with a logical address (identity) of the grain, but not its physical address.
When you call a method using the grain reference, then the grain will get activated in a silo (if it is not already activated in the cluster).
Your method calls on the grain reference are sent to the activation by the Orleans runtime.

A call to `GetGrain`  is an inexpensive local operation of constructing a grain reference with an embedded identity of the target grain.

## Virtual methods 

A grain class can optionally override `OnActivateAsync` and `OnDeactivateAsync` virtual methods that get invoked by the Orleans runtime upon activation and deactivation of each grain of the class.
This gives the grain code a chance to perform additional initialization and cleanup operations.
An exception throw by `OnActivateAsync` fails the activation process.
While `OnActivateAsync`, if overridden, is always called as part of the grain activation process, `OnDeactivateAsync` is not guaranteed to get called in all situations, for example, in case of a server failure or other abnormal events.
Because of that, applications should not rely on `OnDeactivateAsync` for performing critical operations, such as persistence of state changes, and only use it for best effort operations.

## Declarative persistence 

If a grain class uses declarative persistence by inheriting from `Grain<T>`, the Orleans runtime automatically loads state of grain of that class as part of the activation process by calling `ReadStateAsync` before invoking `OnActivateAsync` and processing a request to the grain.
A failure to load grain's state with `ReadStateAsync` fails the activation process.
The Orleans runtime never automatically persists grain's state, and leaves it up to the application code when to call `WriteStateAsync`'

## Sequence of events in a grain's life cycle

The life cycle of a grain is like this

- Another grain or a client calls a method of the grain
- The grain gets activated (if it is not activated anywhere in the silo) and an instance of the grain class is created
  - Constructor of the grain is executed and DI will setup (If you have DI)
  - If declarative persistence is used, the grain state is read from storage
  - If overridden, `OnActivateAsync` is called
- The grain processes incoming requests
- The grain remains idle for some time
- Runtime decides to deactivate the grain
- Runtime calls `OnDeactivateAsync`
- Runtime removes the grain from memory

Upon a graceful shutdown of a silo, all grain activations it holds get deactivated.
Any requests waiting to be processed in grains' queues get forwarded to other silos in the cluster, where new activations of deactivated grains get created on an as-needed basis.
If a silo shuts down or dies ungracefully, other silos in the cluster detect the failure, and start creating new activations of grains lost on the failed silo, as new requests for those grains arrive.
Note that detection of a silo failure takes some time (configurable), and hence the process of reactivating lost grains isn't instantaneous. 

## Next
Next we look at how easy it is to debug our Orleans application

[Debugging](Debugging.md)
