---
layout: page
title: Developing a Client
---
{% include JB/setup %}

Once we have our grain type implemented, we can write a client application that uses the type.

The following Orleans DLLs from either the `[SDK-ROOT]\Binaries\PresenceClient_ or _[SDK-ROOT]\Samples\References` directories need to be referenced in the client application project:

* Orleans.dll
* OrleansRuntimeInterfaces.dll

Almost any client will involve use of the grain factory class.
The `GetGrain()` method is used for getting a grain reference for a particular ID.
As was already mentioned, grains cannot be explicitly created or deleted.

``` csharp
GrainClient.Initialize();

// Hardcoded player ID
Guid playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}");
IPlayerGrain player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerId);

IGameGrain game = player.CurrentGame.Result;
var watcher = new GameObserver();
var observer = GrainClient.GrainFactory.CreateObjectReference<IGameObserver>(watcher);
await game.SubscribeForGameUpdates();
```

If this code is used from the main thread of a console application, you have to call `Wait()` on the task returned by `game.SubscribeForGameUpdates()` because `await` does not prevent the `Main()` function from returning, which will cause the client process to exit.

See the Key Concepts section for more details on the various ways to use `Task`s for execution scheduling and exception flow.

## Find or create grains

After establishing a connection by calling `GrainClient.Initialize()`, static methods in the generic factory class may be used to get a reference to a grain, such as `GrainClient.GrainFactory.GetGrain<IPlayerGrain>()` for the `PlayerGrain`. The grain interface is passed as a type argument to `GrainFactory.GetGrain<T>()`.

## Sending messages to grains

The programming model for communicating with grains from a client is almost the same as from a grain.
The client holds grain references which implement a grain interface like `IPlayerGrain`.
It invokes methods on that grain reference, and these return asynchronous values: `Task`/`Task<T>`, or another grain interface inheriting from `IGrain`.
The client can use the `await` keyword or `ContinueWith()` method to queue continuations to be executed when these asynchronous values resolve, or the `Wait()` method to block the current thread.

The one key difference between communicating with a grain from within a client or from within another grain is the single-threaded execution model.
Grains are constrained to be single-threaded by the Orleans scheduler, while clients may be multi-threaded.
The client library uses the TPL thread pool to manage continuations and callbacks, and so it is up to the client to manage its own concurrency using whatever synchronization constructs are appropriate for its environment – locks, events, TPL tasks, etc.

## Receiving notifications

There are situations in which a simple message/response pattern is not enough, and the client needs to receive asynchronous notifications.
For example, a user might want to be notified when a new message has been published by someone that she is following.

An observer is a one-way asynchronous interface that inherits from `IGrainObserver`, and all its methods must be `void`.
The grain sends a notification to the observer by invoking it like a grain interface method, except that it has no return value, and so the grain need not depend on the result.
The Orleans runtime will ensure one-way delivery of the notifications.
A grain that publishes such notifications should provide an API to add or remove observers.

To subscribe to a notification, the client must first create a local C# object that implements the observer interface.
It then calls `CreateObjectReference()` method on the grain factory, to turn the C# object into a grain reference, which can then be passed to the subscription method on the notifying grain.

This model can also be used by other grains to receive asynchronous notifications.
Unlike in the client subscription case, the subscribing grain simply implements the observer interface as a facet, and passes in a reference to itself (e.g. `this.AsReference<IChirperViewer>`).

## Example

Here is an extended version of the example given above of a client application that connects to Orleans, finds the player account, subscribes for updates to the game session the player is part of, and prints out notifications until the program is manually terminated.

``` csharp
namespace PlayerWatcher
{
    class Program
    {
        /// <summary>
        /// Simulates a companion application that connects to the game
        /// that a particular player is currently part of, and subscribes
        /// to receive live notifications about its progress.
        /// </summary>
        static void Main(string[] args)
        {
            try
            {
                GrainClient.Initialize();

                // Hardcoded player ID
                Guid playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}");
                IPlayerGrain player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerId);
                IGameGrain game = null;

                while (game == null)
                {
                    Console.WriteLine("Getting current game for player {0}...", playerId);

                    try
                    {
                        game = player.CurrentGame.Result;
                        if (game == null) // Wait until the player joins a game
                            Thread.Sleep(5000);
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("Exception: ", exc.GetBaseException());
                    }
                }

                Console.WriteLine("Subscribing to updates for game {0}...", game.GetPrimaryKey());

                // Subscribe for updates
                var watcher = new GameObserver();
                game.SubscribeForGameUpdates(GrainClient.GrainFactory.CreateObjectReference<IGameObserver>(watcher)).Wait();

                // .Wait will block main thread so that the process doesn't exit.
                // Updates arrive on thread pool threads.
                Console.WriteLine("Subscribed successfully. Press <Enter> to stop.");
                Console.ReadLine();
            }
            catch (Exception exc)
            {
                Console.WriteLine("Unexpected Error: {0}", exc.GetBaseException());
            }
        }

        /// <summary>
        /// Observer class that implements the observer interface.
        /// Need to pass a grain reference to an instance of this class to subscribe for updates.
        /// </summary>
        private class GameObserver : IGameObserver
        {
            // Receive updates
            public void UpdateGameScore(string score)
            {
                Console.WriteLine("New game score: {0}", score);
            }
        }
    }
}
```

## Next

[Running the Application](Running-the-Application)
