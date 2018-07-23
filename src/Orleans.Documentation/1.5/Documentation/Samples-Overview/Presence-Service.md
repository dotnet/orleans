---
layout: page
title: Presence Service
---

[!include[](../../warning-banner.md)]

# Presence Service

A presence service serves as the hub of many social applications, including multi-player games and chats. Its essential function is to know who is online at given point in time and alert other users of their online presence. Each logged-in player sends a "heartbeat pulse" at regular intervals, 

In this section we walk through the steps involved in defining and using a new `Player` grain type.
The grain type we define will have one property that returns a reference to the game the player is currently in, and two methods for joining and leaving a game.

We will create three separate pieces of code: the grain interface definition, the grain implementation, and a standard C# class that uses the grain.
Each of these belongs in a different project, built into a different DLL: the interface needs to be available on both the "client" and "server" sides, while the implementation class should be hidden from the client, and the client class from the server.

The interface project should be created using the Visual Studio "Orleans Grain Interface Collection" template that is included in the Orleans SDK, and the grain implementation project should be created using the Visual Studio "Orleans Grain Class Collection" template.
The grain client project can use any standard .NET code project template, such as the standard Console Application or Class Library templates.

A grain cannot be explicitly created or deleted.
It always exists "virtually" and is activated automatically when a request is sent to it.
A grain has either a GUID, string or a long integer key within the grain type.
Application code creates a reference to a grain by calling the `GetGrain<TGrainType>(Guid id)` or `GetGrain<TGrainType>(long id)` or other overloads of a generic grain factory methods for a specific grain identity.
The `GetGrain()` call is a purely local operation to create a grain reference.
It does not trigger creation of a grain activation and has not impact on its life cycle.
A grain activation is automatically created by the Orleans runtime upon a first request sent to the grain.

A grain interface must inherit from one of the `IGrainWithXKey`.interfaces where X is the type of the key used.
The GUID, string or long integer key of a grain can later be retrieved via the `GetPrimaryKey()` or `GetPrimaryKeyLong()` extension methods, respectively.

## Defining the Grain Interface

A grain type is defined by an interface that inherits from one of the `IGrainWithXKey` marker interfaces like 'IGrainWithGuidKey' or 'IGrainWithStringKey'.

All of the methods in the grain interface must return a `Task` or a `Task<T>`.
The underlying type `T` for value `Task` must be serializable.

 Example:

``` csharp
public interface IPlayerGrain : IGrainWithGuidKey
{
  Task<IGameGrain> GetCurrentGame();
  Task JoinGame(IGameGrain game);
  Task LeaveGame(IGameGrain game);
}
```

## Using the Grain Factory

After the grain interface has been defined, building the project originally created with the Orleans Visual Studio project template will use the Orleans-specific MSBuild targets to generate a client proxy classes corresponding to the user-defined grain interfaces and to merge this additional code back into the interface DLL.

Application should use the generic grain factory class to get references to grains. Inside the grain code, the factory is available via the protected GrainFactory class member property. On the client side the factory is available via the `GrainClient.GrainFactory` static field.

When running inside a grain the following code should be used to get the grain reference:

``` csharp
    this.GrainFactory.GetGrain<IPlayerGrain>(grainKey);
```
When running on the Orleans client side the following code should be used to get the grain reference:

``` csharp
    GrainClient.GrainFactory.GetGrain<IPlayerGrain>(grainKey);
```

## The Implementation Class

A grain type is materialized by a class that implements the grain type’s interface and inherits directly or indirectly from `Orleans.Grain`.

The `PlayerGrain` grain class implements the `IPlayerGrain` interface.

``` csharp
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
