---
layout: page
title: Tutorial 1 Hello World
---

# Tutorial 1: Hello World

This process recreates the same Hello World sample application available [here](https://github.com/dotnet/orleans/tree/master/Samples/2.0/HelloWorld).

## Overview of the parts

This application consists of a solution that contains four projects: SiloHost, OrleansClient, HelloWorld.Interfaces, and HelloWorld.Grains.
We will create the SiloHost and OrleansClient projects as .NET Core App files and we will create the HelloWorld.Interfaces and HelloWorld.Grains projects as .NET Standard Libraries.
Orleans comes into the picture when we add the NuGet packages to each of these four projects and replace the default code with the Orleans code provided in this tutorial.
Finally, we will configure the dependency references among the projects before building and running the solution.

## How the parts work together

SiloHost is started first.
Then, OrleansClient is started. OrleansClient tells the system to create a grain based on the grain interface provided.
The grain is then stored in SiloHost. OrleansClient sends a greeting to the grain.
The grain returns a response to OrleansClient, which OrleansClient displays on the console.

## Step One: Create the project structure

1. In Visual Studio, create a new Visual C# Console App (.NET Core) project.
2. Name the project **SiloHost** and name the solution **HelloWorld**.
3. Add a second .NET Core Console App project to the HelloWorld solution and name it **OrleansClient**.
4. Add another new project, but this time choose .NET Standard – Class Library. Name it **HelloWorld.Interfaces**.
5. Add a new interface class file to the GrainInterfaces folder and name it **IHello**.
  *Note: If  Visual Studio adds a default class named `Class1.cs`, feel free to delete this file to keep things tidy.
6. For the fourth and final project, add another .NET Standard – Class Library project and name it **HelloWorld.Grains**.
7. Inside the Grains project, add a class and name it **HelloGrain.cs**.

The structure of your Solution should look like this:

```

Solution 'HelloWorld' (4 projects)
> HelloWorld.Grains
  > Dependencies
  > HelloGrain.cs
> HelloWorld.Interfaces
  > Dependencies
  > IHello.cs
> OrleansClient
  > Dependencies
  > Program.cs
> SiloHost
  > Dependencies
  > Program.cs

```

## Step Two: Add Orleans Packages using NuGet

Right-click on each project and select **Manage NuGet Packages…**
Search for and install the packages for each project, as listed here:

SiloHost

- Microsoft.Orleans.OrleansProviders
- Microsoft.Orleans.OrleansRuntime
- Microsoft.Extensions.Logging.Console

OrleansClient

- Microsoft.Orleans.Core
- Microsoft.Extensions.Logging.Console

HelloWorld.Grains

- Microsoft.Orleans.Core.Abstractions
- Microsoft.Orleans.OrleansCodeGenerator.Build
- Microsoft.Extensions.Logging.Abstractions

GrainInterfaces
- Microsoft.Orleans.Core.Abstractions
- Microsoft.Orleans.OrleansCodeGenerator.Build

## Step Three: Configure project dependencies

The SiloHost, OrleansClient, and HelloWorld.Grain projects each have dependencies on other projects in the solution.
HelloWorld.Interfaces does not have any dependencies.

SiloHost

- HelloWorld.Grains
- HelloWorld.Interfaces

OrleansClient

- HelloWorld.Interfaces

HelloWorld.Grains

- HelloWorld.Interfaces

1. To add a dependency, right-click the project and select **Add…** -> **Reference…**.
2. Check the boxes for the projects you need and click **OK.**

## Step Four: Replace the default code

### SiloHost - Program.cs

```csharp
using System;
using System.Net;
using System.Threading.Tasks;
using HelloWorld.Grains;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace OrleansSiloHost
{
    public class Program
    {
        public static int Main(string[] args)
        {
            return RunMainAsync().Result;
        }

        private static async Task<int> RunMainAsync()
        {
            try
            {
                var host = await StartSilo();
                Console.WriteLine("Press Enter to terminate...");
                Console.ReadLine();

                await host.StopAsync();

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static async Task<ISiloHost> StartSilo()
        {
            // define the cluster configuration
            var builder = new SiloHostBuilder()
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "dev";
                    options.ServiceId = "HelloWorldApp";
                })
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(HelloGrain).Assembly).WithReferences())
                .ConfigureLogging(logging => logging.AddConsole());

            var host = builder.Build();
            await host.StartAsync();
            return host;
        }
    }
}


```

### OrleansClient - Program.cs

```csharp
using HelloWorld.Interfaces;
using Orleans;
using Orleans.Runtime;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace OrleansClient
{
    /// <summary>
    /// Orleans test silo client
    /// </summary>
    public class Program
    {
        const int initializeAttemptsBeforeFailing = 5;
        private static int attempt = 0;

        static int Main(string[] args)
        {
            return RunMainAsync().Result;
        }

        private static async Task<int> RunMainAsync()
        {
            try
            {
                using (var client = await StartClientWithRetries())
                {
                    await DoClientWork(client);
                    Console.ReadKey();
                }

                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Console.ReadKey();
                return 1;
            }
        }

        private static async Task<IClusterClient> StartClientWithRetries()
        {
            attempt = 0;
            IClusterClient client;
            client = new ClientBuilder()
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "dev";
                    options.ServiceId = "HelloWorldApp";
                })
                .ConfigureLogging(logging => logging.AddConsole())
                .Build();

            await client.Connect(RetryFilter);
            Console.WriteLine("Client successfully connect to silo host");
            return client;
        }

        private static async Task<bool> RetryFilter(Exception exception)
        {
            if (exception.GetType() != typeof(SiloUnavailableException))
            {
                Console.WriteLine($"Cluster client failed to connect to cluster with unexpected error.  Exception: {exception}");
                return false;
            }
            attempt++;
            Console.WriteLine($"Cluster client attempt {attempt} of {initializeAttemptsBeforeFailing} failed to connect to cluster.  Exception: {exception}");
            if (attempt > initializeAttemptsBeforeFailing)
            {
                return false;
            }
            await Task.Delay(TimeSpan.FromSeconds(4));
            return true;
        }

        private static async Task DoClientWork(IClusterClient client)
        {
            // example of calling grains from the initialized client
            var friend = client.GetGrain<IHello>(0);
            var response = await friend.SayHello("Good morning, my friend!");
            Console.WriteLine("\n\n{0}\n\n", response);
        }
    }
}


```

### HelloWorld.Interfaces - IHello.cs

```csharp
using System.Threading.Tasks;

namespace HelloWorld.Interfaces
{
    /// <summary>
    /// Orleans grain communication interface IHello
    /// </summary>
    public interface IHello : Orleans.IGrainWithIntegerKey
    {
        Task<string> SayHello(string greeting);
    }
}


```

### HelloWorld.Grain - HelloGrain.cs

```csharp
using HelloWorld.Interfaces;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HelloWorld.Grains
{
    /// <summary>
    /// Orleans grain implementation class HelloGrain.
    /// </summary>
    public class HelloGrain : Orleans.Grain, IHello
    {
        private readonly ILogger logger;

        public HelloGrain(ILogger<HelloGrain> logger)
        {
            this.logger = logger;
        }  

        Task<string> IHello.SayHello(string greeting)
        {
            logger.LogInformation($"SayHello message received: greeting = '{greeting}'");
            return Task.FromResult($"You said: '{greeting}', I say: Hello!");
        }
    }
}

```

## Step Five: Deploy the Solution

### Using Visual Studio

#### Set startup projects

You can configure VisualStudio to start the SiloHost and OrleansClient projects simultaneously.
Right-click the solution in the Solution Explorer, select `Set StartUp Projects...`.
Select the `Multiple startup projects` option.
Select SiloHost and move it to the top of the list and then choose Start from the Action menu.
Then move OrleansClient to be just below SiloHost and set its Action to Start.
Click Ok.
Start the project.
Two console windows open and display logging information.
The last line should be the message: "You said: 'Good morning, my friend!', I say: Hello!"

#### Command Line

Alternatively, you can run from the command line.

To start the silo:

```
dotnet run --project src\SiloHost

```

To start the client (you will have to use a different command window)

```
dotnet run --project src\OrleansClient\

```

### Using Windows PowerShell

Add this PowerShell script to the HelloWorld directory, set the execution policy as needed, and run the script.

#### BuildAndRun.ps1

```ps1
# First build the Orleans vNext NuGet packages locally
if((Test-Path "..\..\vNext\Binaries\Debug\") -eq $false) { 
     # this will only work in Windows. 
     # Alternatively build the NuGet packages and place them in the <root>/vNext/Binaries/Debug folder
     # (or make sure there is a package source available with the Orleans 2.0 TP NuGets)
    #..\..\Build.cmd netstandard
}

# Uncomment the following to clear the NuGet cache if rebuilding the packages doesn't seem to take effect.
#dotnet nuget locals all --clear

dotnet restore
if ($LastExitCode -ne 0) { return; }

dotnet build --no-restore
if ($LastExitCode -ne 0) { return; }

# Run the 2 console apps in different windows

Start-Process "dotnet" -ArgumentList "run --project src/SiloHost --no-build"
Start-Sleep 10
Start-Process "dotnet" -ArgumentList "run --project src/OrleansClient --no-build"

```
