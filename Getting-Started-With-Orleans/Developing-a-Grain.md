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

A grain reference is a logical endpoint that allows other grains, as well as an [Orleans Client](/Orleans/Getting-Started-With-Orleans/Clients) code, to invoke methods and properties of a particular grain interface implemented by a grain. 
A grain reference is a proxy object that implements the corresponding grain interface. 
A grain reference can be constructed by passing the identity of the grain to the `GetGrain()` method of the factory class auto-generated at compile time for the corresponding grain interface, or receiving the return value of a method or property. 
A grain reference can be passed as an argument to a method call.

##Next

[Developing a Client](Developing-a-Client)


