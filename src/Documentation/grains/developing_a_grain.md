---
layout: page
title: Developing a Grain
---

### Setup

Before you write code to implement a grain class, create a new Class Library project targeting .NET 4.6.1 or higher in Visual Studio and add the `Microsoft.Orleans.OrleansCodeGenerator.Build` NuGet package to it.

```
PM> Install-Package Microsoft.Orleans.OrleansCodeGenerator.Build
```

### Grain Interfaces and Classes

Grains interact with each other and get called from outside by invoking methods declared as part of the respective grain interfaces.
A grain class implements one or more previously declared grain interfaces.
All methods of a grain interface must return a `Task` (for `void` methods) or a `Task<T>` (for methods returning values of type `T`).

The following is an excerpt from the Orleans version 1.5 Presence Service sample:

```csharp
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
       Console.WriteLine(
           "Player {0} joined game {1}", 
           this.GetPrimaryKey(),
           game.GetPrimaryKey());

       return Task.CompletedTask;
    }

   // Game grain calls this method to notify that the player has left the game.
   public Task LeaveGame(IGameGrain game)
   {
       currentGame = null;
       Console.WriteLine(
           "Player {0} left game {1}",
           this.GetPrimaryKey(),
           game.GetPrimaryKey());

       return Task.CompletedTask;
   }
}
```

### Returning Values from Grain Methods

A grain method that returns a value of type `T` is defined in a grain interface as returning a `Task<T>`.
For grain methods not marked with the `async` keyword, when the return value is available, it is usually returned via the following statement:
```csharp
public Task<SomeType> GrainMethod1()
{
    ...
    return Task.FromResult(<variable or constant with result>);
}
```

A grain method that returns no value, effectively a void method, is defined in a grain interface as returning a `Task`.
The returned `Task` indicates asynchronous execution and completion of the method.
For grain methods not marked with the `async` keyword, when a "void" method completes its execution, it needs to return the special value of `Task.CompletedTask`:
```csharp
public Task GrainMethod2()
{
    ...
    return Task.CompletedTask;
}
```

A grain method marked as `async` returns the value directly:
```csharp
public async Task<SomeType> GrainMethod3()
{
    ...
    return <variable or constant with result>;
}
```
A "void" grain methods marked as `async` that returns no value simply returns at the end of their execution:
```csharp
public async Task GrainMethod4()
{
    ...
    return;
}
```

If a grain method receives the return value from another asynchronous method call, to a grain or not, and doesn't need to perform error handling of that call, it can simply return the `Task` it receives from that asynchronous call as its return value:
```csharp
public Task<SomeType> GrainMethod5()
{
    ...
    Task<SomeType> task = CallToAnotherGrain();
    return task;
}
```
Similarly, a "void" grain method can return a `Task` returned to it by another call instead of awaiting it.
```csharp
public Task GrainMethod6()
{
    ...
    Task task = CallToAsyncAPI();
    return task;
}
```

### Grain Reference

A Grain Reference is a proxy object that implements the same grain interface as the corresponding grain class.
It encapsulates a logical identity (type and unique key) of the target grain.
A grain reference is what is used for making calls to the target grain.
Each grain reference is for a single grain (a single instance of the grain class), but one can create multiple independent references for the same grain.

Since a grain reference represents a logical identity of the target grain, it is independent from the physical location of the grain, and stays valid even after a complete restart of the system.
Developers can use grain references like any other .NET object.
It can be passed to a method, used as a method return value, etc., and even saved to persistent storage. 

A grain reference can be obtained by passing the identity of a grain to the `GrainFactory.GetGrain<T>(key)` method, where `T` is the grain interface and `key` is the unique key of the grain within the type. 

The following are examples of how to obtain a grain reference of the `IPlayerGrain` interface defined above.

From inside a grain class:

```csharp
    //construct the grain reference of a specific player
    IPlayerGrain player = GrainFactory.GetGrain<IPlayerGrain>(playerId);
```

From Orleans Client code.

Prior to 1.5.0:
```csharp
    IPlayerGrain player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerId);
```

Since 1.5.0:
```csharp
    IPlayerGrain player = client.GetGrain<IPlayerGrain>(playerId);
```

### Grain Method Invocation

The Orleans programming model is based on the [Asynchronous Programming with Async and Await](https://msdn.microsoft.com/en-us/library/hh191443.aspx).

Using the grain reference from the previous example, here's how one performs a grain method invocation:

```csharp
//Invoking a grain method asynchronously
Task joinGameTask = player.JoinGame(this);
//The await keyword effectively makes the remainder of the method execute asynchronously at a later point (upon completion of the Task being awaited) without blocking the thread.
await joinGameTask;
//The next line will execute later, after joinGameTask is completed.
players.Add(playerId);

```

It is possible to join two or more `Tasks`; the join operation creates a new `Task` that is resolved when all of its constituent `Task`s are completed.
This is a useful pattern when a grain needs to start multiple computations and wait for all of them to complete before proceeding.
For example, a front-end grain that generates a web page made of many parts might make multiple back-end calls, one for each part, and receive a `Task` for each result.
The grain would then await the join of all of these `Tasks`; when the join `Task` is resolved, the individual `Task`s have been completed, and all the data required to format the web page has been received.

Example:

``` csharp
List<Task> tasks = new List<Task>();
Message notification = CreateNewMessage(text);

foreach (ISubscriber subscriber in subscribers)
{
   tasks.Add(subscriber.Notify(notification));
}

// WhenAll joins a collection of tasks, and returns a joined Task that will be resolved when all of the individual notification Tasks are resolved.
Task joinedTask = Task.WhenAll(tasks);
await joinedTask;

// Execution of the rest of the method will continue asynchronously after joinedTask is resolve.
```

### Virtual methods

A grain class can optionally override `OnActivateAsync` and `OnDeactivateAsync` virtual methods that get invoked by the Orleans runtime upon activation and deactivation of each grain of the class.
This gives the grain code a chance to perform additional initialization and cleanup operations.
An exception thrown by `OnActivateAsync` fails the activation process.
While `OnActivateAsync`, if overridden, is always called as part of the grain activation process, `OnDeactivateAsync` is not guaranteed to get called in all situations, for example, in case of a server failure or other abnormal events.
Because of that, applications should not rely on OnDeactivateAsync for performing critical operations, such as persistence of state changes, and only use it for best effort operations.

