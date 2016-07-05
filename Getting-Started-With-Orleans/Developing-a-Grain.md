---
layout: page
title: Developing a Grain
---


Please read about [Grains](/orleans/Getting-Started-With-Orleans/Grains) before reading this article.

## Grain Interfaces

Grains interact with each other by invoking methods declared as part of the respective grain interfaces.
A grain class implements one or more previously declared grain interfaces.
All methods of a grain interface must return a `Task` (for `void` methods) or a `Task<T>` (for methods returning values of type `T`).

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
    public Task<IGameGrain> GetCurrentGame()
    {
       return Task.FromResult(currentGame);
    }

    // Game grain calls this method to notify that the player has joined the game.
    public Task JoinGame(IGameGrain game)
    {
       currentGame = game;
       Console.WriteLine("Player {0} joined game {1}", this.GetPrimaryKey(), game.GetPrimaryKey());
       return TaskDone.Done;
    }

   // Game grain calls this method to notify that the player has left the game.
   public Task LeaveGame(IGameGrain game)
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

## Grain Method Invocation

The Orleans programming model is based on Asynchronous Programming with async and await. A detailed article about the subject is [here](https://msdn.microsoft.com/en-us/library/hh191443.aspx).

Using the grain reference from the previous example, the following is an example of grain method invocation:

```csharp
//Invoking a grain method asynchronously
Task joinGameTask = player.JoinGame(this);
//The `await` keyword effectively turns the remainder of the method into a closure that will asynchronously execute upon completion of the Task being awaited without blocking the executing thread.
await joinGameTask;
//The next lines will be turned into a closure by the C# compiler.
players.Add(playerId);

```

It is possible to join two or more `Task`s; the join creates a new `Task` that is resolved when all of its constituent `Task`s are completed. This is a useful pattern when a grain needs to start multiple computations and wait for all of them to complete before proceeding.
For example, a front-end grain that generates a web page made of many parts might make multiple back-end calls, one for each part, and receive a `Task` for each result.
The grain would then wait for the join of all of these `Task`s; when the join is resolved, the individual `Task`s have been completed, and all the data required to format the web page has been received.

Example:

``` csharp
List<Task> tasks = new List<Task>();
ChirperMessage chirp = CreateNewChirpMessage(text);
foreach (IChirperSubscriber subscriber in Followers.Values)
{
   tasks.Add(subscriber.NewChirpAsync(chirp));
}
Task joinedTask = Task.WhenAll(tasks);
await joinedTask;
```

## TaskDone.Done Utility Property

There is no "standard" way to conveniently return an already completed "void" `Task`, so Orleans sample code defines `TaskDone.Done` for that purpose.

## Next

[Developing a Client](Developing-a-Client)


