---
layout: page
title: Shutting down Orleans
---

This document explains how to gracefully shutdown an Orleans silo before application exit, first as a Console app, and then as a Docker container app.

# Graceful shutdown - Console app
The following code shows how to gracefully shutdown an Orleans silo console app in response to the user pressing Ctrl+C, which generates the `Console.CancelkeyPress` event.

Normally when that event handler returns, the application will exit immediately, causing a catastrophic Orleans silo crash and loss of in-memory state.
But in the sample code below, we set `a.Cancel = true;` to prevent the application closing before the Orleans silo has completed its graceful shutdown.

```csharp
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MySiloHost {

    class Program {

        static readonly ManualResetEvent _siloStopped = new ManualResetEvent(false);

        static ISiloHost silo;
        static bool siloStopping = false;
        static readonly object syncLock = new object();

        static void Main(string[] args) {

            SetupApplicationShutdown();

            silo = CreateSilo();
            silo.StartAsync().Wait();

            /// Wait for the silo to completely shutdown before exiting. 
            _siloStopped.WaitOne();
        }

        static void SetupApplicationShutdown() {
            /// Capture the user pressing Ctrl+C
            Console.CancelKeyPress += (s, a) => {
                /// Prevent the application from crashing ungracefully.
                a.Cancel = true;
                /// Don't allow the following code to repeat if the user presses Ctrl+C repeatedly.
                lock (syncLock) {
                    if (!siloStopping) {
                        siloStopping = true;
                        Task.Run(StopSilo).Ignore();
                    }
                }
                /// Event handler execution exits immediately, leaving the silo shutdown running on a background thread,
                /// but the app doesn't crash because a.Cancel has been set = true
            };
        }

        static ISiloHost CreateSilo() {
            return new SiloHostBuilder()
                .Configure(options => options.ClusterId = "MyTestCluster")
                /// Prevent the silo from automatically stopping itself when the cancel key is pressed.
                .Configure<ProcessExitHandlingOptions>(options => options.FastKillOnProcessExit = false)
                .UseDevelopmentClustering(options => options.PrimarySiloEndpoint = new IPEndPoint(IPAddress.Loopback, 11111))
                .ConfigureLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddConsole())
                .Build();
        }

        static async Task StopSilo() {
            await silo.StopAsync();
            _siloStopped.Set();
        }
    }
}
```

Of course, there are many other ways of achieving the same goal. 
Below is shown a way, popular online, and misleading, that DOES NOT work properly. It does not work because it sets up a race condition between two methods trying to exit first: the `Console.CancelKeyPress` event handler method, and the `static void Main(string[] args)` method. 
When the event handler method finishes first, which happens at least half the time, the application will hang instead of exiting smoothly.

```csharp
class Program {

    static readonly ManualResetEvent _siloStopped = new ManualResetEvent(false);

    static ISiloHost silo;
    static bool siloStopping = false;
    static readonly object syncLock = new object();

    static void Main(string[] args) {

        Console.CancelKeyPress += (s, a) => {
            Task.Run(StopSilo);
            /// Wait for the silo to completely shutdown before exiting. 
            _siloStopped.WaitOne();
            /// Now race to finish ... who will finish first?
            /// If I finish first, the application will hang! :(
        };

        silo = CreateSilo();
        silo.StartAsync().Wait();

        /// Wait for the silo to completely shutdown before exiting. 
        _siloStopped.WaitOne();
        /// Now race to finish ... who will finish first?
    }

    static async Task StopSilo() {
        await silo.StopAsync();
        _siloStopped.Set();
    }
}
```

# Graceful shutdown - Docker app
To be completed.
