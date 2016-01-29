---
layout: page
title: Developing a Grain
---
{% include JB/setup %}

## Grain Interfaces

Grains interact with each other by invoking methods declared as part of the respective grain interfaces. 
A grain class implements one or more previously declared grain interfaces. 
All methods of a grain interface are required to be asynchronous. 
That is, their return types have to be `Task`s (see [Asynchrony and Tasks](Asynchrony-and-Tasks) for more details). 

The following is an excerpt from the [Presence Service](/orleans/Samples-Overview/Presence-Service) sample: 

``` csharp
//an example of a Grain Interface
public interface IPlayerGrain : IGrainWithGuidKey 
{ 
  Task<IGameGrain> GetCurrentGame();
  Task JoinGame(IGameGrain game); 
  Task LeaveGame(IGameGrain game); 
} 

//an example of a Grain class implementing a Grain Interface
public class PlayerGrain : Grain, IPlayerGrain 
{ 
    private IGameGrain currentGame; 

    // Game the player is currently in. May be null. 
    public Task<IGameGrain> GetCurrentGameAsync()
    { 
       return Task.FromResult(currentGame);
    } 

    // Game grain calls this method to notify that the player has joined the game. 
    public Task JoinGameAsync(IGameGrain game) 
    {
       currentGame = game; 
       Console.WriteLine("Player {0} joined game {1}", this.GetPrimaryKey(), game.GetPrimaryKey()); 
       return TaskDone.Done; 
    } 

   // Game grain calls this method to notify that the player has left the game. 
   public Task LeaveGameAsync(IGameGrain game) 
   { 
       currentGame = null; 
       Console.WriteLine("Player {0} left game {1}", this.GetPrimaryKey(), game.GetPrimaryKey()); 
       return TaskDone.Done; 
   } 
} 
```

## Grain Reference

A Grain Reference is a proxy object that implements the same grain interface implemented by the corresponding grain class. Using asynchronous messaging, it provides full-duplex communication with other grains, as well as [Orleans Client](/Orleans/Getting-Started-With-Orleans/Clients) code.
A grain reference can be constructed by passing the identity of a grain to the `GrainFactory.GetGrain<T>()` method, where T is the grain interface. Developers can use grain references like any other .NET object. It can be passed to a method, used as a method return value, etc.

The following are examples of how to construct a grain reference of the `IPlayerGrain` interface defined above.

In Orleans Client code:

```csharp
    //construct the grain reference of a specific player
    IPlayerGrain player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerId);
```
From inside a Grain class:

```csharp
    //construct the grain reference of a specific player
    IPlayerGrain player = GrainFactory.GetGrain<IPlayerGrain>(playerId);
```
##Next

[Developing a Client](Developing-a-Client)


