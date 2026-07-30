---
title: Request context
description: Learn about request context in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Request context

The <xref:Orleans.Runtime.RequestContext> is an Orleans feature allowing application metadata, such as a trace ID, to flow with requests. You can add application metadata on the client; it flows with Orleans requests to the receiving grain. The feature is implemented by a public static class, <xref:Orleans.Runtime.RequestContext>, in the Orleans namespace. This class exposes two simple methods:

```csharp
void Set(string key, object value)
```

Use the preceding API to store a value in the request context. The value can be any serializable type.

```csharp
object Get(string key)
```

Use the preceding API to retrieve a value from the current request context.

The backing storage for <xref:Orleans.Runtime.RequestContext> is async-local. When a caller (client-side or within Orleans) sends a request, the contents of the caller's <xref:Orleans.Runtime.RequestContext> are included with the Orleans message for the request. When the grain code receives the request, that metadata is accessible from the local <xref:Orleans.Runtime.RequestContext>. If the grain code doesn't modify the <xref:Orleans.Runtime.RequestContext>, then any grain it requests receives the same metadata, and so on.

Application metadata is also maintained when you schedule a future computation using <xref:System.Threading.Tasks.TaskFactory.StartNew*> or <xref:System.Threading.Tasks.Task.ContinueWith*>. In both cases, the continuation executes with the same metadata as the scheduling code had when the computation was scheduled. That is, the system copies the current metadata and passes it to the continuation, so the continuation won't see changes made after the call to <xref:System.Threading.Tasks.TaskFactory.StartNew*> or <xref:System.Threading.Tasks.Task.ContinueWith*>.

> [!IMPORTANT]
> Application metadata doesn't flow back with responses. Code that runs as a result of receiving a response (either within a <xref:System.Threading.Tasks.Task.ContinueWith*> continuation or after a call to <xref:System.Threading.Tasks.Task.Wait*> or <xref:System.Threading.Tasks.Task`1.Result>) still runs within the current context set by the original request.

For example, to set a trace ID in the client to a new <xref:System.Guid?displayProperty=nameWithType>, call:

```csharp
RequestContext.Set("TraceId", Guid.NewGuid());
```

Within grain code (or other code running within Orleans on a scheduler thread), you could use the trace ID of the original client request, for instance, when writing a log:

```csharp
Logger.LogInformation(
    "Currently processing external request {TraceId}",
    RequestContext.Get("TraceId"));
```

While you can send any serializable `object` as application metadata, it's worth mentioning that large or complex objects might add noticeable overhead to message serialization time. For this reason, we recommend using simple types (strings, GUIDs, or numeric types).

## Example grain code

To help illustrate the use of request context, consider the following example grain code:

```csharp
using GrainInterfaces;
using Microsoft.Extensions.Logging;

namespace Grains;

public class HelloGrain(ILogger<HelloGrain> logger) : Grain, IHelloGrain
{
    ValueTask<string> IHelloGrain.SayHello(string greeting)
    {
        _logger.LogInformation("""
            SayHello message received: greeting = "{Greeting}"
            """,
            greeting);

        var traceId = RequestContext.Get("TraceId") as string
            ?? "No trace ID";

        return ValueTask.FromResult($"""
            TraceID: {traceId}
            Client said: "{greeting}", so HelloGrain says: Hello!
            """);
    }
}

public interface IHelloGrain : IGrainWithStringKey
{
    ValueTask<string> SayHello(string greeting);
}
```

The `SayHello` method logs the incoming `greeting` parameter and then retrieves the trace ID from the request context. If no trace ID is found, the grain logs "No trace ID".

## Example client code

The client can set the trace ID in the request context before calling the `SayHello` method on the `HelloGrain`. The following client code demonstrates how to set a trace ID in the request context and call the `SayHello` method on the `HelloGrain`:

```csharp
﻿using GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using var host = Host.CreateDefaultBuilder(args)
    .UseOrleansClient(clientBuilder =>
        clientBuilder.UseLocalhostClustering())
    .Build();

await host.StartAsync();

var client = host.Services.GetRequiredService<IClusterClient>();

var grain = client.GetGrain<IHelloGrain>("friend");

var id = "example-id-set-by-client";

RequestContext.Set("TraceId", id);

var message = await friend.SayHello("Good morning!");

Console.WriteLine(message);
// Output:
//   TraceID: example-id-set-by-client
//   Client said: "Good morning!", so HelloGrain says: Hello!
```

In this example, the client sets the trace ID to "example-id-set-by-client" before calling the `SayHello` method on the `HelloGrain`. The grain retrieves the trace ID from the request context and logs it.

## Example placement access code

The <xref:Orleans.Runtime.RequestContext> data can be accessed during placement or placement filtering through the <xref:Orleans.Runtime.Placement.PlacementTarget> `RequestContextData`. The static <xref:Orleans.Runtime.RequestContext> is not populated at this time as there is not yet an activation of the grain. The following placement filter code demonstrates how to get <xref:Orleans.Runtime.RequestContext> data while handling placement:

```csharp
internal sealed class ExamplePlacementFilterDirector(ILogger<ExamplePlacementFilterDirector> logger)
    : IPlacementFilterDirector
{
    public IEnumerable<SiloAddress> Filter(
        PlacementFilterStrategy filterStrategy,
        PlacementTarget target,
        IEnumerable<SiloAddress> silos)
    {
        if (target.RequestContextData.TryGetValue("somekey", out var somevalue) 
            && somevalue is string somestring)
        {
            logger.LogInformation("Read {Value} for {Key} from the RequestContext", somestring, "somekey");
            // somestring is available for the filtering logic
        }
        return silos;
    }
}
```

In this example, the "somekey" value is read from the <xref:Orleans.Runtime.Placement.PlacementTarget> `RequestContextData` during the placement filtering.
