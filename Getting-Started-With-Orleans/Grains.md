---
layout: page
title: Grains
---
{% include JB/setup %}

## Grains (Actors): Units of Distribution

Grains are the building blocks of an Orleans application, they are atomic units of isolation, distribution, and persistence. 
A typical grain encapsulates state and behavior of a single entity (e.g. a specific user).
A grain executes at most one logical unit of work, known as a [Turn](/orleans/Getting-Started-With-Orleans/Asynchrony-and-Tasks), at a time.
This means that there is no need to use locks or other local synchronization mechanisms in grain code.
The only way for two grains to interact is by sending messages. They have no shared memory or other shared state.

### A Grain Activation - The runtime instance of a Grain

When there is work for a grain, Orleans ensures there is an instance of the grain on one of [Orleans Silos](Silos). When there is no instance of the grain on any silo, the run-time creates one. This process is called Activation. In case a grain is using [Grain Persistence](Orleans/Getting-Stated-With-Orleans/Grain-Persistence), the run-time automatically reads the state from the backing-store upon activation. 
Orleans controls the process of activating and deactivating grains transparently. When coding a grain, a developer assumes all grains are always activated.

### Activation modes

Orleans supports two modes: single activation mode (default), in which only one simultaneous activation of every grain is created, and stateless worker mode, in which independent activations of a grain are created to increase the throughput. 
"Independent" implies that there is no state reconciliation between different activations of the same grain. 
So this mode is appropriate for grains that hold no local state, or grains whose local state is static, such as a grain that acts as a cache of persistent state

## Next
Next we look at Silos, a unit for hosting grains.

[Silos](Silos)
