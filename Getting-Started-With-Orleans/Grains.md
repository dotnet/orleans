---
layout: page
title: Grains
---
{% include JB/setup %}

## Grains (Actors): Units of Distribution

Grains are building blocks of an Orleans application. Grains are the atomic units of isolation, distribution, and persistence. A grain encapsulates state and behavior, like any .NET object. A grain is a logical entity that at any point in time may have zero or more (usually not more than one) in-memory replicas called activations. A grain may exist only in the persistent store with no in-memory replicas if there are no requests pending for the grain. When there is work for the grain, the run-time will create an activation of the grain by picking a server and instantiating there the .NET class that implements the behavior of the grain. 

 Orleans controls the process of activating and deactivating grains. This process is transparent to the developer: when coding a grain, a developer should assume that any other grains that the current grain will interact with are activated. 

 Grains are isolated; the only way for two grains to interact is by sending messages. They have no shared memory or other shared state. Each activation of a grain executes at most one logical unit of work, known as a turn, at a time. This means that there is no need to use locks or other local synchronization mechanisms in grain code.

 In this release, Orleans supports two modes: single activation mode (default), in which only one simultaneous activation of every grain is created, and stateless worker mode, in which independent activations of a grain are created to increase the throughput. “Independent” implies that there is no state reconciliation between different activations of the same grain. So this mode is appropriate for grains that hold no local state, or grains whose local state is static, such as a grain that acts as a cache of persistent state

## Grain Interfaces

Grains interact with each other by invoking methods declared as part of the respective grain interfaces. A grain implements one or more previously declared grain interfaces. All methods of a grain interface are required to be asynchronous. That is, their return types have to be `Task`s (see Asynchrony and Tasks for more details). 

Example:

``` csharp
public interface IChirperPublisher : IGrain 
{ 
  Task<long> GetUserIdAsync(); 
  Task<string> GetUserAliasAsync();
  Task<string> GetDisplayNameAsync();
  Task<List<ChirperMessage>> GetPublishedMessagesAsync(int n = 10, int start = 0); 
  Task AddFollowerAsync(string userAlias, IChirperSubscriber follower); 
  Task RemoveFollowerAsync(string userAlias, IChirperSubscriber follower); 
} 
```

## Grain Reference

A grain reference is a logical endpoint that allows other grains, as well as non-grain client code, to invoke methods and properties of a particular grain interface implemented by a grain. A grain reference is a proxy object that implements the corresponding grain interface. A grain reference can be constructed by passing the identity of the grain to the `GetGrain()` method of the factory class auto-generated at compile time for the corresponding grain interface, or receiving the return value of a method or property. A grain reference can be passed as an argument to a method call.