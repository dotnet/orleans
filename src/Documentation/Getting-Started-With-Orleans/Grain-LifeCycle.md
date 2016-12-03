---
layout: page
title: Grain LifeCycle
---


## Grain LifeCycle

We say grains virtually always exist but here we want to describe different stages of a grain's life.
A grain is like a class definition, it is nothing unless it is on the memory and can execute code.
An instance of a class is instantiated with the `new` keyword in C#.
an instance of a grain is called an activation.
The grain's instances are differentiated by their key and a grain with a specific key is either activated or not.
You don't control this by using construction/destruction keywords and instead Orleans runtime manages this for you.

All possible grains in the whole key space virtually always exist
and it means whenever you call a method of a grain, if it is not active, it will get activated by the runtime.
Also after the grain passes a certain amount of time in idle state, the runtime will deactivate the grain (i.e. destroys it).
A grain is in idle state if it is not currently processing a message and its cueue of messages is empty.

## callbacks 

Just like the class which can have constructors and (destructors in some languages), grains have
`OnActivateAsync` and `OnDeactivateAsync` virtual methods which you can override.

## Different grain types and their differences regarding life cycle

Your grains can be persistent and store their state in storage
or have no state which is stored in storage. 
In case of the later, all activations are created equal and there is no difference between them.
For grains which store their state and derive from `Grain<T>` there is a point which you should consider.

> `ReadStateAsync` is called before calling `OnActivateAsync` and if it fails then the grain will not be activated.


## Sequence of events in a grain's life cycle

So the life cycle of a grain is like this

- another grain or a client calls a method of a grain
- the grain gets activated and brought to memory
  - the grain state will be read from storage if any
  - if successful then `OnActivateAsync` is called
- The grain will process some messages
- The grain remains idle for some time
- Runtime will call `OnDeactivateAsync`
- Runtime removes the grain from memory

Now if a Silo gets a shutdown request gracefully, the message cueues of all activated grains will be forwarded to another silo with the grain activation,
but if a silo crashes, `OnDeactivateAsync` will not be called so you can not rely on it to store state for fault tolerance unless you can live with losing some state.

## Next
Next we look at how easy it is to debug our Orleans application

[Debugging](Debugging.md)
