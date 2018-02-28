---
layout: page
title: Configuration of Orleans 2.0
---

# Configuration of Orleans 2.0

Some of the biggest changes in Orleans 2.0 were made in the area of configuration of silos and clients. The goals were to make the API more fluid and intuitive by aligning the developers experience with how ASP.NET Core is configured.

For a working sample application that targets Orleans 2.0 see: https://github.com/dotnet/orleans/tree/master/Samples/HelloWorld.NetCore
The sample hosts the client and the silo in .NET Core console applications that work in different platforms, but the same can be done with .NET Framework 4.6.1+ console applications (that work only on Windows).

## Configuring and Starting a Silo (using the new SiloBuilder API)

A silo is configured programmatically via a `SiloHostBuilder` and a number of supplemental option classes.
Option classes in Orleans follow the [ASP.NET Options](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options) pattern, and can be loaded via files, environment variables etc. Please refer to the [Options pattern documentation]((https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options)) for more information.

For local development, please refer to the below example of how to configure a silo for that case.
It configures and starts a silo listening on `loopback' address and 11111 and 30000 as silo and gateway ports respectively.

Add the `Microsoft.Orleans.Server` NuGet meta-package to the project. After you get comfortable with the API, you can pick and choose which exact packages included in `Microsoft.Orleans.Server` you actually need, and reference them instead.

```PowerShell
PM> Install-Package Microsoft.Orleans.Server
```

You need to configure `ClusterOptions` via `ISiloBuilder.Configure`method, specify that you want `DevelopmentClustering` as your clustering choice with this silo being the primary, and then configure silo endpoints.
*We are looking to improve the local development configuration experience down to a single method call before we release a final build of 2.0.0.*

`ConfigureApplicationParts` call explicitly adds the assembly with grain classes to the application setup.
It also adds any referenced assembly due to the `WithReferences` extension.
After these steps are completed, the silo host gets built and the silo gets started.

You can create an empty console application project targeting .NET Framework 4.6.1 or higher for hosting a silo, as well as a .NET Core console application.

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
         // define the cluster configuration
        var siloPort = 11111;
        int gatewayPort = 30000;
        var siloAddress = IPAddress.Loopback; 
        var builder = new SiloHostBuilder()
            //configure ClusterOptions to set CluserId and ServiceId
            .Configure(options => options.ClusterId = "helloworldcluster")
            //Configure local primary silo using DevelopmentClustering
            .UseDevelopmentClustering(options => options.PrimarySiloEndpoint = new IPEndPoint(siloAddress, siloPort))
            //Configure silo endpoint and gatewayport
            .ConfigureEndpoints(siloAddress, siloPort, gatewayPort)
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
A client for connecting to a cluster of silos and sending requests to grains is configured programmatically via a `ClientBuilder` and a number of supplemental option classes.
Like silo options, client option classes follow the [ASP.NET Options](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options).

For local development, please refer to the below example of how to configure a client for that case.
It configures a client that would connect to a `loopback` silo.

Add the `Microsoft.Orleans.Client` NuGet meta-package to the project. After you get comfortable with the API, you can pick and choose which exact packages included in `Microsoft.Orleans.Client` you actually need, and reference them instead.
```PowerShell
PM> Install-Package Microsoft.Orleans.Client
```

You need to configure `ClientBuilder` with a cluster ID that matches the one you specified for local silo and specify static clustering as your clustering choice pointing it to the gateway port of the silo
*We are looking to improve the local development configuration experience down to a single method call before we release a final build of 2.0.0.*

`ConfigureApplicationParts` call explicitly adds the assembly with grain interfaces to the application setup.

After these steps are completed, we can build the client and `Connect()` method on it to connect to the cluster.

You can create an empty console application project targeting .NET Framework 4.6.1 or higher for running a client or reuse the console application project you created for hosting a silo.

Here is an example of how a client can connect to a local silo:

```csharp
//define siloAddress and gatewayPort, should be consistent with whatever used in configuring silo.
var siloAddress = IPAddress.Loopback;
var gatewayPort = 30000;
client = new ClientBuilder()
    //Configure ClusterOptions
    .ConfigureCluster(options => options.ClusterId = "helloworldcluster")
    //Use StaticClustering in client side
    .UseStaticClustering(options => options.Gateways = new List<Uri>(){ (new IPEndPoint(siloAddress, gatewayPort)).ToGatewayUri() })
    // Add assemblies to scan for grains interfaces and serializers.
    // For more info read the Application Parts section
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IHello).Assembly))
    .ConfigureLogging(logging => logging.AddConsole())
var client = builder.Build();
await client.Connect();
```

## Application Parts

Orleans 2.0 does not perform automatic folder scanning to discover user assemblies and types.
Instead, those assemblies are provided explicitly during the configuration stage.
These assemblies are referred to as Application Parts.
All Grains, Grain Interfaces, and Serializers are discovered using Application Parts.
*For backward compatibility, if none of the Application Parts methods is called, the runtime will scan all assemblies in the silo or client folder.
It is recommended not to rely on this fallback behavior, and explicitly specify all necessary application assemblies instead.*

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
