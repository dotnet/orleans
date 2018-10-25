---
layout: page
title: Tutorial 1 Hello World
---

# Tutorial 1: Hello World

This process ties into the Hello World sample application available [here](https://github.com/dotnet/orleans/tree/master/Samples/2.0/HelloWorld).

The main concepts of Orleans involve a silo, a client, and one or more grains.
Creating an Orleans app involves configuring the silo, configuring the client, and writing the grains.

## Configuring the silo

Silos are configured programmatically via `SiloHostBuilder` and a number of supplemental option classes.
A list of all of the options can be found [here.](http://dotnet.github.io/orleans/Documentation/clusters_and_clients/configuration_guide/list_of_options_classes.html)

```csharp
[...]
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
```

| Option | Used for |
|-------------|----------|
| `.UseLocalhostClustering()` | Declaring that we are using a single local silo |
| `ClusterOptions` | ClusterId is the name for the Orleans cluster must be the same for silo and client so they can talk to each other. ServiceId is the ID used for the application and it must not change across deployments|
| `EndpointOptions` | This tells the silo where to listen. For this example, we are using a `loopback`. |
| `ConfigureApplicationParts` | Adds the assembly with grain classes to the application setup. |

After loading the configurations, the SiloHost is built and then started asynchronously.

## Configuring the client

Similar to the silo, the client is configured via `ClientBuilder` and a similar collection of option classes.

```csharp
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

```

| Option | Used for |
|-------------|----------|
| `.UseLocalhostClustering()` | Same as for SiloHost |
| `ClusterOptions` | Same as for SiloHost |

A more in-depth guide to configuring your client can be found [in the Client Configuration section of the Configuration Guide.](http://dotnet.github.io/orleans/Documentation/clusters_and_clients/configuration_guide/client_configuration.html)

## Writing a grain

Grains are the building blocks of an Orleans application, and you can read more about them in the [Core Concepts section of the Orleans documentation.](http://dotnet.github.io/orleans/Documentation/core_concepts/index.html)

This is the main body of code for the Hello World grain:

```csharp
[...]
namespace HelloWorld.Grains
{
    public class HelloGrain : Orleans.Grain, IHello
    {
        Task<string> IHello.SayHello(string greeting)
        {
            logger.LogInformation($"SayHello message received: greeting = '{greeting}'");
            return Task.FromResult($"You said: '{greeting}', I say: Hello!");
        }
    }
}
```

A grain class implements one or more grain interfaces, as you can read [here, in the Grains section.](http://dotnet.github.io/orleans/Documentation/grains/index.html))

```csharp
[...]
namespace HelloWorld.Interfaces
{
    public interface IHello : Orleans.IGrainWithIntegerKey
    {
        Task<string> SayHello(string greeting);
    }
}

```



## How the parts work together

SiloHost is started first.
Then, OrleansClient is started.
OrleansClient creates a reference to the IHello grain and calls its SayHello() method through its interface, IHello.
This programming model is built as part of our core concept of distributed Object Oriented Programming.
Next, the grain is activated in the silo.
OrleansClient sends a greeting to the activated grain.
The grain returns a response to OrleansClient, which OrleansClient displays on the console.

## Create the project structure

1. In Visual Studio, create a new Visual C# Console App (.NET Core) project.
2. Name the project **SiloHost** and name the solution **HelloWorld**.
3. Add a second .NET Core Console App project to the HelloWorld solution and name it **OrleansClient**.
4. Add another new project, but this time choose .NET Standard – Class Library. Name it **HelloWorld.Interfaces**.
5. Add a new interface class file to the GrainInterfaces folder and name it **IHello**.
  *Note: If  Visual Studio adds a default class named `Class1.cs`, feel free to delete this file to keep things tidy.
6. For the fourth and final project, add another .NET Standard – Class Library project and name it **HelloWorld.Grains**.
7. Inside the Grains project, add a class and name it **HelloGrain.cs**.
8. Add the Orleans NuGet packages
