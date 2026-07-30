---
title: "Tutorial: Hello world"
description: Explore the hello world tutorial project written with .NET Orleans.
ms.date: 01/21/2026
ms.topic: tutorial
zone_pivot_groups: orleans-version
---

# Tutorial: Hello world

This overview ties into the [Hello World sample application](https://github.com/dotnet/samples/tree/main/orleans/HelloWorld).

The main concepts of Orleans involve a silo, a client, and one or more grains. Creating an Orleans app involves configuring the silo, configuring the client, and writing the grains.

## Configure the silo

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0,orleans-7-0"

Configure silos programmatically via <xref:Orleans.Hosting.ISiloBuilder> and several supplemental option classes. You can find a list of all options at [List of options classes](../host/configuration-guide/list-of-options-classes.md).

:::code language="csharp" source="snippets/helloworld/SiloProgram.cs" id="silo_setup":::

The preceding code:

- Creates a default host builder.
- Calls <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> to configure the silo.
- Uses localhost clustering for local development.
- Configures the cluster and service IDs.
- Configures the endpoint to listen on loopback.
- Adds console logging.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Configure silos programmatically via <xref:Orleans.Hosting.ISiloHostBuilder> and several supplemental option classes. You can find a list of all options at [List of options classes](../host/configuration-guide/list-of-options-classes.md).

:::code language="csharp" source="../snippets-v3/helloworld/SiloProgram.cs" id="silo_setup":::

:::zone-end

| Option                      | Used for |
| --------------------------- | -------- |
| `.UseLocalhostClustering()` | Configures the client to connect to a silo on the localhost. |
| <xref:Orleans.Configuration.ClusterOptions>            | <xref:Orleans.Configuration.ClusterOptions.ClusterId> is the name for the Orleans cluster; it must be the same for the silo and client so they can communicate. <xref:Orleans.Configuration.ClusterOptions.ServiceId> is the ID used for the application and must not change across deployments. |
| <xref:Orleans.Configuration.EndpointOptions>           | Tells the silo where to listen. For this example, use `loopback`. |

:::zone target="docs" pivot="orleans-3-x"

| Option                      | Used for |
| --------------------------- | -------- |
| `ConfigureApplicationParts` | Adds the grain class and interface assembly as application parts to your Orleans application. This is not needed in Orleans 7.0+ as source generators handle this automatically. |

:::zone-end

After loading the configurations, build the host and then start it asynchronously.

## Configure the client

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0,orleans-7-0"

Similar to the silo, configure the client via <xref:Orleans.IClientBuilder> and a similar collection of option classes.

:::code language="csharp" source="snippets/helloworld/ClientProgram.cs" id="client_setup":::

The preceding code:

- Creates a default host builder.
- Calls <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> to configure the client.
- Uses localhost clustering to connect to the local silo.
- Configures the cluster and service IDs to match the silo.
- Starts the host and retrieves the <xref:Orleans.IClusterClient> from the service provider.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Similar to the silo, configure the client via <xref:Orleans.IClientBuilder> and a similar collection of option classes.

:::code language="csharp" source="../snippets-v3/helloworld/ClientProgram.cs" id="client_setup":::

:::zone-end

| Option                      | Used for               |
| --------------------------- | ---------------------- |
| `.UseLocalhostClustering()` | Same as for the silo |
| <xref:Orleans.Configuration.ClusterOptions>            | Same as for the silo |

Find a more in-depth guide to configuring your client in the [Client configuration](../host/configuration-guide/client-configuration.md) section of the Configuration Guide.

## Write a grain

Grains are the key primitives of the Orleans programming model. They are the building blocks of an Orleans application, serving as atomic units of isolation, distribution, and persistence. Grains are objects representing application entities. Just like in classic Object-Oriented Programming, a grain encapsulates an entity's state and encodes its behavior in code logic. Grains can hold references to each other and interact by invoking methods exposed via interfaces.

Read more about them in the [Grains](../grains/index.md) section of the Orleans documentation.

This is the main body of code for the Hello World grain:

:::code language="csharp" source="snippets/helloworld/HelloGrain.cs" id="hello_grain":::

A grain class implements one or more grain interfaces. For more information, see the [Grains](../grains/index.md) section.

:::code language="csharp" source="snippets/helloworld/IHello.cs" id="ihello_interface":::

## How the parts work together

This programming model builds on the core concept of distributed Object-Oriented Programming. Start the <xref:Orleans.Hosting.ISiloHost> first. Then, start the `OrleansClient` program. The `Main` method of `OrleansClient` calls the method that starts the client, `StartClientWithRetries()`. Pass the client to the `DoClientWork()` method.

:::code language="csharp" source="snippets/helloworld/ClientProgram.cs" id="do_client_work":::

At this point, `OrleansClient` creates a reference to the `IHello` grain and calls its `SayHello()` method via the `IHello` interface. This call activates the grain in the silo. `OrleansClient` sends a greeting to the activated grain. The grain returns the greeting as a response to `OrleansClient`, which then displays it on the console.

## Run the sample app

To run the sample app, refer to the [Readme](https://github.com/dotnet/samples/tree/main/orleans/HelloWorld).
