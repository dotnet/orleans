---
layout: page
title: Configuration of Orleans 2.0
---

# Configuration of Orleans 2.0

Some of the biggest changes in Orleans 2.0 were made in the area of configuration of silos and clients. The goals were to make the API more fluid and intuitive by aligning the developers experience with how ASP.NET Core is configured.

For a working sample application that targets Orleans 2.0 see: https://github.com/dotnet/orleans/tree/master/Samples/HelloWorld.NetCore
The sample hosts the client and the silo in .NET Core console applications that work in different platforms, but the same can be done with .NET Framework 4.6.1+ console applications (that work only on Windows).

## Configuring and Starting a Silo (using the new SiloBuilder API)

*Note: Orleans 2.0.0-beta2 is still changing, and it still requires some configuration to be done on the legacy `ClusterConfiguration` object, such as for configuring providers, or the IP endpoints to listen on. By the time 2.0.0 final is released, there will no longer be a requirement to use these legacy configuration objects, althought they will be provided in some form for backwards compatibility.*

A silo is configured programmatically via via a `ClientConfiguration` object and a `SiloHostBuilder`.
For local testing, the easiest way to go is to start by using the `ClusterConfiguration.LocalhostPrimarySilo()` helper method.
The configuration object is then passed to a new instance of `SiloHostBuilder` class, that can be built to create a host object that can be started and stopped after that.

You can create an empty console application project targeting .NET Framework 4.6.1 or higher for hosting a silo, as well as a .NET Core console application.
Add the `Microsoft.Orleans.Server` NuGet meta-package to the project.

```PowerShell
PM> Install-Package Microsoft.Orleans.Server
```

Here is an example of how a local silo can be started:

```csharp
public class Program
{
    public static async Task Main(string[] args)
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
        // define the cluster configuration (temporarily required in the beta version,
        // will not be required by the final release)
        var config = ClusterConfiguration.LocalhostPrimarySilo();
        // add providers to the legacy configuration object.
        config.AddMemoryStorageProvider();

        var builder = new SiloHostBuilder()
            .UseConfiguration(config)
            // Add assemblies to scan for grains and serializers.
            // For more info read the Application Parts section
            .ConfigureApplicationParts(parts =>
                parts.AddApplicationPart(typeof(HelloGrain).Assembly)
                     .WithReferences())
            // Configure logging with any logging framework that supports Microsoft.Extensions.Logging.
            // In this particular case it logs using the Microsoft.Extensions.Logging.Console package.
            .ConfigureLogging(logging => logging.AddConsole());

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }
}
```

## Configuring and Connecting a Client (using the new ClientBuilder API)

*Note: Orleans 2.0.0-beta2 is still changing, and it still requires some configuration to be done on the legacy `ClientConfiguration` object, such as for configuring providers. By the time 2.0.0 final is released, there will no longer be a requirement to use these legacy configuration objects, althought they will be provided in some form for backwards compatibility.*

Client for connecting to a cluster of silos and sending requests to grains is configured programmatically via a `ClientConfiguration` object and a `ClientBuilder`.
`ClientConfiguration` object can be instantiated and populated directly, load settings from a file, or created with several available helper methods for different deployment environments.
For local testing, the easiest way to go is to use `ClientConfiguration.LocalhostSilo()` helper method.
The configuration object is then passed to a new instance of `ClientBuilder` class.

`ClientBuilder` exposes more methods for configuring additional client features.
After that `Build` method of the `ClientBuilder` object is called to get an implementation of `IClusterClient` interface.
Finally, we call `Connect()` method on the returned object to connect to the cluster.

You can create an empty console application project targeting .NET Framework 4.6.1 or higher for running a client or reuse the console application project you created for hosting a silo.
Add the `Microsoft.Orleans.Client` NuGet meta-package to the project.

```PowerShell
PM> Install-Package Microsoft.Orleans.Client
```

Here is an example of how a client can connect to a local silo:

```csharp
// define the client configuration (temporarily required in the beta version,
// will not be required by the final release)
var config = ClientConfiguration.LocalhostSilo();
var builder = new ClientBuilder()
    .UseConfiguration(config)
    // Add assemblies to scan for grains interfaces and serializers.
    // For more info read the Application Parts section
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IHello).Assembly))
    .ConfigureLogging(logging => logging.AddConsole())
var client = builder.Build();
await client.Connect();
```

## Application Parts

Orleans 2.0 does not perform automatic folder scanning to discover user assemblies and types. Instead, those assemblies are provided explicitly during the configuration stage. These assemblies are refered to as Application Parts. All Grains, Grain Interfaces, and Serializers are discovered using Application Parts.

Application Parts are configured using an `IApplicationPartsManager`, which can be accessed using the `ConfigureApplicationParts` extension method on `IClientBuilder` and `ISiloHostBuilder`. The `ConfigureApplicationParts` method accepts a delegate, `Action<IApplicationPartManager>`.

The following extension methods on `IApplicationPartManager` support common uses:
* `AddApplicationPart(assembly)` a single assembly can be added using this extension method.
* `AddFromAppDomain()` adds all assemblies currently loaded in the `AppDomain`.
* `AddFromApplicationBaseDirectory()` loads and adds all assemblies in the current base path (see `AppDomain.BaseDirectory`).

Assemblies added by the above methods can be supplemented using the following extension methods on their return type, `IApplicationPartManagerWithAssemblies`:
* `WithReferences()` adds all referenced assemblies from the added parts. This immediately loads any transitively referenced assemblies. Assembly loading errors are ignored.
* `WithCodeGeneration()` generates support code for the added parts and adds it to the part manager. Note that this requires the `Microsoft.Orleans.OrleansCodeGenerator` package to be installed, and is commonly referred to as runtime code generation.

See the client and silo configuration sections above for examples.

Type discovery requires that the provided Application Parts include specific attributes. Adding the build-time code generation package (`Microsoft.Orleans.OrleansCodeGenerator.Build`) to each project containing Grains, Grain Interfaces, or Serializers is the recommended approach for ensuring that these attributes are present. Build-time code generation only supports C#. For F#, Visual Basic, and other .NET languages, code can be generated during configuration time via the `WithCodeGeneration()` method described above.

## Logging Configuration

Orleans 2.0 uses the same logging abstractions as ASP.NET Core 2.0. For more information on how to use or configure logging, please read https://docs.microsoft.com/en-us/aspnet/core/fundamentals/logging/

For information on how to migrate the legacy logging configuration or use the legacy logging abstractions from 1.5 to 2.0, please read [Migration from Orleans 1.5 to 2.0](Migration1.5.md)
