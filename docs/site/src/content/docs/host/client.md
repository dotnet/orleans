---
title: Orleans clients
description: Learn how to write .NET Orleans clients.
ms.date: 01/21/2026
ms.topic: concept-article
zone_pivot_groups: orleans-version
ms.custom: sfi-ropc-nochange
---

# Orleans clients

A client allows non-grain code to interact with an Orleans cluster. Clients enable application code to communicate with grains and streams hosted in a cluster. There are two ways to obtain a client, depending on where you host the client code: in the same process as a silo or in a separate process. This article discusses both options, starting with the recommended approach: co-hosting client code in the same process as grain code.

## Co-hosted clients

If you host client code in the same process as grain code, you can directly obtain the client from the hosting application's dependency injection container. In this case, the client communicates directly with the silo it's attached to and can take advantage of the silo's extra knowledge about the cluster.

This approach provides several benefits, including reduced network and CPU overhead, decreased latency, and increased throughput and reliability. The client uses the silo's knowledge of the cluster topology and state and doesn't need a separate gateway. This avoids a network hop and a serialization/deserialization round trip, thereby increasing reliability by minimizing the number of required nodes between the client and the grain. If the grain is a [stateless worker grain](../grains/stateless-worker-grains.md) or happens to be activated on the same silo where the client is hosted, no serialization or network communication is needed at all, allowing the client to achieve additional performance and reliability gains. Co-hosting client and grain code also simplifies deployment and application topology by eliminating the need to deploy and monitor two distinct application binaries.

There are also drawbacks to this approach, primarily that the grain code is no longer isolated from the client process. Therefore, issues in client code, such as blocking I/O or lock contention causing thread starvation, can affect grain code performance. Even without such code defects, *noisy neighbor* effects can occur simply because client code executes on the same processor as grain code, putting additional strain on the CPU cache and increasing contention for local resources. Additionally, identifying the source of these issues becomes more difficult because monitoring systems cannot distinguish logically between client code and grain code.

Despite these drawbacks, co-hosting client code with grain code is a popular option and the recommended approach for most applications. The aforementioned drawbacks are often minimal in practice for the following reasons:

- Client code is often very *thin* (e.g., translating incoming HTTP requests into grain calls). Therefore, *noisy neighbor* effects are minimal and comparable in cost to the otherwise required gateway.
- If a performance issue arises, your typical workflow likely involves tools such as CPU profilers and debuggers. These tools remain effective in quickly identifying the source of the issue, even with both client and grain code executing in the same process. In other words, while metrics become coarser and less able to precisely identify the issue's source, more detailed tools are still effective.

### Obtain a client from a host

If you host using the [.NET Generic Host](../../core/extensions/generic-host.md), the client is automatically available in the host's [dependency injection](../../core/extensions/dependency-injection/overview.md) container. You can inject it into services such as [ASP.NET controllers](/aspnet/core/mvc/controllers/actions) or <xref:Microsoft.Extensions.Hosting.IHostedService> implementations.

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Alternatively, you can obtain a client interface like <xref:Orleans.IGrainFactory> or <xref:Orleans.IClusterClient> from <xref:Orleans.Hosting.ISiloHost>:

:::zone-end

```csharp
var client = host.Services.GetService<IClusterClient>();
await client.GetGrain<IMyGrain>(0).Ping();
```

## External clients

Client code can run outside the Orleans cluster where grain code is hosted. In this case, an external client acts as a connector or conduit to the cluster and all the application's grains. Typically, you use clients on frontend web servers to connect to an Orleans cluster serving as a middle tier, with grains executing business logic.

In a typical setup, a frontend web server:

- Receives a web request.
- Performs necessary authentication and authorization validation.
- Decides which grain(s) should process the request.
- Uses the [Microsoft.Orleans.Client](https://www.nuget.org/packages/Microsoft.Orleans.Client) NuGet package to make one or more method calls to the grain(s).
- Handles successful completion or failures of the grain calls and any returned values.
- Sends a response to the web request.

### Initialize a grain client

Before you can use a grain client to make calls to grains hosted in an Orleans cluster, you need to configure, initialize, and connect it to the cluster.

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

Provide configuration via <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> and several supplemental option classes containing a hierarchy of configuration properties for programmatically configuring a client. For more information, see [Client configuration](configuration-guide/client-configuration.md).

Consider the following example of a client configuration:

### [Microsoft Entra ID (recommended)](#tab/entra-id)

Using a `TokenCredential` with a service URI is the recommended approach. This pattern avoids storing secrets in configuration and leverages Microsoft Entra ID for secure authentication.

<xref:Azure.Identity.DefaultAzureCredential> provides a credential chain that works seamlessly across local development and production environments. During development, it uses your Azure CLI or Visual Studio credentials. In production on Azure, it automatically uses the managed identity assigned to your resource.

[!INCLUDE [credential-chain-guidance](../includes/credential-chain-guidance.md)]

```csharp
using Azure.Identity;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "my-first-cluster";
        options.ServiceId = "MyOrleansService";
    });

    clientBuilder.UseAzureStorageClustering(options =>
    {
        options.ConfigureTableServiceClient(
            new Uri("https://<your-storage-account>.table.core.windows.net"),
            new DefaultAzureCredential());
    });
});

using var host = builder.Build();
await host.StartAsync();
```

### [Connection string](#tab/connection-string)

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "my-first-cluster";
        options.ServiceId = "MyOrleansService";
    });

    clientBuilder.UseAzureStorageClustering(
        options => options.ConfigureTableServiceClient(connectionString));
});

using var host = builder.Build();
await host.StartAsync();
```

---

When you start the `host`, the client is configured and available through its constructed service provider instance.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Provide configuration via <xref:Orleans.ClientBuilder> and several supplemental option classes containing a hierarchy of configuration properties for programmatically configuring a client. For more information, see [Client configuration](configuration-guide/client-configuration.md).

Example of a client configuration:

:::code language="csharp" source="snippets-v3/client/ClientExamples.cs" id="client_config":::

Finally, you need to call the `Connect()` method on the constructed client object to connect it to the Orleans cluster. It's an asynchronous method returning a <xref:System.Threading.Tasks.Task>, so you need to wait for its completion using `await` or `.Wait()`.

:::code language="csharp" source="snippets-v3/client/ClientExamples.cs" id="client_connect":::

:::zone-end

### Make calls to grains

Making calls to grains from a client is no different from [making such calls from within grain code](../grains/index.md). Use the same <xref:Orleans.IGrainFactory.GetGrain``1(System.Type,System.Guid)?displayProperty=nameWithType> method (where `T` is the target grain interface) in both cases [to obtain grain references](../grains/grain-references.md). The difference lies in which factory object invokes <xref:Orleans.IGrainFactory.GetGrain*?displayProperty=nameWithType>. In client code, you do this through the connected client object, as the following example shows:

```csharp
IPlayerGrain player = client.GetGrain<IPlayerGrain>(playerId);
Task joinGameTask = player.JoinGame(game)

await joinGameTask;
```

A call to a grain method returns a <xref:System.Threading.Tasks.Task> or <xref:System.Threading.Tasks.Task`1>, as required by the [grain interface rules](../grains/index.md). The client can use the `await` keyword to asynchronously await the returned <xref:System.Threading.Tasks.Task> without blocking the thread, or in some cases, use the `Wait()` method to block the current thread of execution.

The major difference between making calls to grains from client code and from within another grain is the single-threaded execution model of grains. The Orleans runtime constrains grains to be single-threaded, while clients can be multi-threaded. Orleans doesn't provide any such guarantee on the client-side, so it's up to the client to manage its concurrency using appropriate synchronization constructs for its environment—locks, events, `Tasks`, etc.

### Receive notifications

Sometimes, a simple request-response pattern isn't sufficient, and the client needs to receive asynchronous notifications. For example, a user might want notification when someone they follow publishes a new message.

Using [Observers](../grains/observers.md) is one mechanism enabling exposure of client-side objects as grain-like targets to be invoked by grains. Calls to observers don't provide any indication of success or failure, as they are sent as one-way, best-effort messages. Therefore, it's the responsibility of your application code to build a higher-level reliability mechanism on top of observers where necessary.

Another mechanism for delivering asynchronous messages to clients is [Streams](../streaming/index.md). Streams expose indications of success or failure for individual message delivery, enabling reliable communication back to the client.

### Client connectivity

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

There are two scenarios in which a cluster client can experience connectivity issues:

- When the client attempts to connect to a silo.
- When making calls on grain references obtained from a connected cluster client.

In the first case, the client attempts to connect to a silo. If the client cannot connect to any silo, it throws an exception indicating what went wrong. You can register an <xref:Orleans.IClientConnectionRetryFilter> to handle the exception and decide whether to retry. If you provide no retry filter, or if the retry filter returns `false`, the client gives up permanently.

:::code source="snippets/ClientConnectRetryFilter.cs":::

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

There are two scenarios in which a cluster client can experience connectivity issues:

- When the <xref:Orleans.IClusterClient.Connect?displayProperty=nameWithType> method is called initially.
- When making calls on grain references obtained from a connected cluster client.

In the first case, the `Connect` method throws an exception indicating what went wrong. This is typically (but not necessarily) a <xref:Orleans.Runtime.SiloUnavailableException>. If this happens, the cluster client instance is unusable and should be disposed of. You can optionally provide a retry filter function to the `Connect` method, which could, for instance, wait for a specified duration before making another attempt. If you provide no retry filter, or if the retry filter returns `false`, the client gives up permanently.

If `Connect` returns successfully, the cluster client is guaranteed to be usable until disposed. This means that even if the client experiences connection issues, it attempts to recover indefinitely. You can configure the exact recovery behavior on a <xref:Orleans.Configuration.GatewayOptions> object provided by the <xref:Orleans.ClientBuilder>, e.g.:

:::code language="csharp" source="snippets-v3/client/ClientExamples.cs" id="gateway_options":::

:::zone-end

In the second case, where a connection issue occurs during a grain call, a <xref:Orleans.Runtime.SiloUnavailableException> is thrown on the client-side. You could handle this like so:

:::code source="snippets/Program.cs" id="siloexc":::

The grain reference isn't invalidated in this situation; you could retry the call on the same reference later when a connection might have been re-established.

### Dependency injection

The recommended way to create an external client in a program using the .NET Generic Host is to inject an <xref:Orleans.IClusterClient> singleton instance via dependency injection. This instance can then be accepted as a constructor parameter in hosted services, ASP.NET controllers, etc.

> [!NOTE]
> When co-hosting an Orleans silo in the same process that will be connecting to it, it is *not* necessary to manually create a client; Orleans will automatically provide one and manage its lifetime appropriately.

When connecting to a cluster in a different process (on a different machine), a common pattern is to create a hosted service like this:

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

:::code source="snippets/ClusterClientHostedService.cs":::

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

:::code language="csharp" source="snippets-v3/client/ClusterClientHostedService.cs" id="cluster_client_hosted_service":::

:::zone-end

Register the service like this:

```csharp
await Host.CreateDefaultBuilder(args)
    .UseOrleansClient(builder =>
    {
        builder.UseLocalhostClustering();
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService<ClusterClientHostedService>();
    })
    .RunConsoleAsync();
```

### Example

Here's an extended version of the previous example showing a client application that connects to Orleans, finds the player account, subscribes for updates to the game session the player is part of using an observer, and prints notifications until the program is manually terminated.

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

:::code source="snippets/ExampleExternalProgram.cs" id="program":::

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

:::code language="csharp" source="snippets-v3/client/ExternalClientExample.cs" id="external_client_example":::

:::zone-end
