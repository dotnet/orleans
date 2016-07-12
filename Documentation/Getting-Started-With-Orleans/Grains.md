---
layout: page
title: Grains
---


## Grains (Actors): Units of Distribution

Distributed applications are inherently concurrent, which leads to complexity. One of the things that makes the actor model special and productive is that it helps reduce some of the complexities of having to grapple with concurrency.

Actors accomplish this in two ways:

* By providing single-threaded access to the internal state of an actor instance.
* By not sharing data between actor instances except via message-passing.

Grains are the building blocks of an Orleans application, they are atomic units of isolation, distribution, and persistence.
A typical grain encapsulates state and behavior of a single entity (e.g. a specific user).

### Turns: Units of Execution

The idea behind the single-threaded execution model for actors is that the invokers (remote) take turns "calling" its methods. Thus, a message coming to actor B from actor A will placed in a queue and the associated handler is invoked only when all prior messages have been serviced.

This allows us to avoid all use of locks to protect actor state, as it is inherently protected against data races. However, it may also lead to problems when messages pass back and forth and the message graph forms cycles. If A sends a message to B from one of its methods and awaits its completion, and B sends a message to A, also awaiting its completion, the application will quickly lock up.

### A Grain Activation - The runtime instance of a Grain

When there is work for a grain, Orleans ensures there is an instance of the grain on one of [Orleans Silos](Silos.md). When there is no instance of the grain on any silo, the run-time creates one. This process is called Activation. In case a grain is using [Grain Persistence](Grain-Persistence.md), the run-time automatically reads the state from the backing-store upon activation.
Orleans controls the process of activating and deactivating grains transparently. When coding a grain, a developer assumes all grains are always activated.

A grain activation performs work in chunks and finishes each chunk before it moves on to the next. Chunks of work include method invocations in response to requests from other grains or external clients, and closures scheduled on completion of a previous chunk. The basic unit of execution corresponding to a chunk of work is known as a turn.

While Orleans may execute many turns belonging to different activations in parallel, each activation will always execute its turns one at a time. This means that there is no need to use locks or other synchronization methods to guard against data races and other multi-threading hazards. As mentioned above, however, the unpredictable interleaving of turns for scheduled closures can cause the state of the grain to be different than when the closure was scheduled, so developers must still watch out for interleaving bugs.

### Activation modes

Orleans supports two modes: single activation mode (default), in which only one simultaneous activation of every grain is created, and stateless worker mode, in which independent activations of a grain are created to increase the throughput.
"Independent" implies that there is no state reconciliation between different activations of the same grain.
So this mode is appropriate for grains that hold no local state, or grains whose local state is static, such as a grain that acts as a cache of persistent state

## Next
Next we look at Silos, a unit for hosting grains.

[Silos](Silos.md)
