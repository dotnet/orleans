---
title: Shut down Orleans silos
description: Learn how to shut down .NET Orleans silos.
ms.date: 05/23/2025
ms.topic: how-to
---

# Shut down Orleans silos

This article explains how to gracefully shut down an Orleans silo before the app exits. This applies to apps running as console apps or container apps. Various termination signals can cause an app to shut down, such as <kbd>Ctrl</kbd>+<kbd>C</kbd> (or `SIGTERM`). The following sections explain how to handle these signals.

## Graceful silo shutdown

The following code shows how to gracefully shut down an Orleans silo console app. Consider the following example code:

```csharp
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

await Host.CreateDefaultBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        // Use the siloBuilder instance to configure the Orleans silo.
    })
    .RunConsoleAsync();
```

The preceding code relies on the [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting) and [Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Server) NuGet packages. The <xref:Microsoft.Extensions.Hosting.HostingHostBuilderExtensions.RunConsoleAsync*> extension method extends <xref:Microsoft.Extensions.Hosting.IHostBuilder> to help manage the app's lifetime accordingly, listening for process termination signals and shutting down the silo gracefully.

Internally, the `RunConsoleAsync` method calls <xref:Microsoft.Extensions.Hosting.HostingHostBuilderExtensions.UseConsoleLifetime*>, which ensures the app shuts down gracefully. Orleans is hosted within the .NET Generic Host, so it shuts down when the host application does, regardless of the lifetime model used.

For more information on host shutdown, see [.NET Generic Host: Host shutdown](../../../core/extensions/generic-host.md#host-shutdown).

## See also

- [.NET Generic Host](../../../core/extensions/generic-host.md)
- [Orleans silo lifecycle overview](../silo-lifecycle.md)
