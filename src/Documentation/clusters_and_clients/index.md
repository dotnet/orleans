---
layout: page
title: What is a grain client
---

### What is a Grain Client?

The term "Client" or sometimes "Grain Client" is used for application code that interacts with grains but itself is not part of a grain logic.
Client code runs outside of the cluster of Orleans servers called silos where grains are hosted.
Hence, a client acts as a connector or conduit to the cluster and to all grains of the application.

![](.\images\frontend_cluster.png)

Usually, clients are used on the frontend web servers to connect to an Orleans cluster that serves as a middle tier with grains executing business logic.
In a typical setup, a frontend web server:
* Receives a web request
* Performs necessary authentication and authorization validation
* Decides which grain(s) should process the request
* Uses Grain Client to make one or more method call to the grain(s)
* Handles successful completion or failures of the grain calls and any returned values
* Sends a response for the web request

### Initialization of Grain Client

Before a grain client can be used for making calls to grains hosted in an Orleans cluster, it needs to be configured, initialized, and connected to the cluster.

Configuration is provided via  `ClientBuilder` and a number of supplemental option classes that contain a hierarchy of configuration properties for programmatically configuring a client.

More information can be in the [Client Configuration guide](../clusters_and_clients/configuration_guide/client_configuration.md).

Example of a client configuration:

```csharp

var client = new ClientBuilder()
    // Clustering information
    .Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "my-first-cluster";
        options.ServiceId = "MyOrleansService";
    })
    // Clustering provider
    .UseAzureStorageClustering(options => options.ConnectionString = connectionString)
    // Application parts: just reference one of the grain interfaces that we use
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IValueGrain).Assembly))
    .Build();

```

Lastly, we need to call `Connect()` method on the constructed client object to make it connect to the Orleans cluster. It's an asynchronous method that returns a `Task`. So we need to wait for its completion with an `await` or `.Wait()`.

```csharp

await client.Connect();

```

### Making Calls to Grains

Making calls to grain from a client is really no different from [making such calls from within grain code](/grains/developing_a_grain.md).
The same `GetGrain<T>(key)` method, where `T` is the target grain interface, is used in both cases [to obtain grain references](/grains/developing_a_grain.md#grain-reference).
The slight difference is in through what factory object we invoke `GetGrain`.
In client code we do that through the connected client object.

``` csharp
IPlayerGrain player = client.GetGrain<IPlayerGrain>(playerId);
Task t = player.JoinGame(game)
await t;
```

A call to a grain method returns a `Task` or a`Task<T>` as required by the [grain interface rules](/grains/developing_a_grain.md).
The client can use the `await` keyword to asynchronously await the returned `Task` without blocking the thread, or in some cases the `Wait()` method to block the current thread of execution.

The major difference between making calls to grains from client code and from within another grain is the single-threaded execution model of grains.
Grains are constrained to be single-threaded by the Orleans runtime, while clients may be multi-threaded.
Orleans does not provide any such guarantee on the client side, and so it is up to the client to manage its own concurrency using whatever synchronization constructs are appropriate for its environment – locks, events, `Tasks`, etc.

### Receiving notifications

There are situations in which a simple request-response pattern is not enough, and the client needs to receive asynchronous notifications.
For example, a user might want to be notified when a new message has been published by someone that she is following.

[Observers](../grains/observers.md) is one such mechanism that enables exposing client side objects as grain-like targets to get invoked by grains.
Calls to observers do not provide any indication of success or failure, as they are sent as one-way best effort message.
So it is a responsibility of the application code to build a higher level reliability mechanism on top of observers where necessary. 

Another mechanism that can be used for delivering asynchronous messages to clients is [Streams](../streaming/index.md). Streams expose indications of success or failure of delivery of individual messages, and hence enable reliable communication back to the client.

### Example

Here is an extended version of the example given above of a client application that connects to Orleans, finds the player account, subscribes for updates to the game session the player is part of with an observer, and prints out notifications until the program is manually terminated.

```csharp
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
            RunWatcher().Wait();
            // Block main thread so that the process doesn't exit.
            // Updates arrive on thread pool threads.
            Console.ReadLine();
        }

        static async Task RunWatcher()
        {
            try

            {
            var client = new ClientBuilder()
                // Clustering information
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "my-first-cluster";
                    options.ServiceId = "MyOrleansService";
                })
                // Clustering provider
                .UseAzureStorageClustering(options => options.ConnectionString = connectionString)
                // Application parts: just reference one of the grain interfaces that we use
                .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IValueGrain).Assembly))
                .Build();

                // Hardcoded player ID
                Guid playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}");
                IPlayerGrain player = client.GetGrain<IPlayerGrain>(playerId);
                IGameGrain game = null;

                while (game == null)
                {
                    Console.WriteLine("Getting current game for player {0}...", playerId);

                    try
                    {
                        game = await player.GetCurrentGame();
                        if (game == null) // Wait until the player joins a game
                        {
                            await Task.Delay(5000);
                        }
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("Exception: ", exc.GetBaseException());
                    }
                }

                Console.WriteLine("Subscribing to updates for game {0}...", game.GetPrimaryKey());

                // Subscribe for updates
                var watcher = new GameObserver();
                await game.SubscribeForGameUpdates(
                    await client.CreateObjectReference<IGameObserver>(watcher));

                Console.WriteLine("Subscribed successfully. Press <Enter> to stop.");
            }
            catch (Exception exc)
            {
                Console.WriteLine("Unexpected Error: {0}", exc.GetBaseException());
            }
        }
    }

    /// <summary>
    /// Observer class that implements the observer interface. Need to pass a grain reference to an instance of this class to subscribe for updates.
    /// </summary>
    class GameObserver : IGameObserver
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