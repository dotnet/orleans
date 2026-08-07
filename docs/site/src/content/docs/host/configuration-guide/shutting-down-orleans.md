---
title: Shut down Orleans silos
description: Gracefully stop Orleans silos with the .NET Generic Host.
ms.date: 08/02/2026
ms.topic: how-to
---

# Shut down Orleans silos

Orleans is an `IHostedService` inside the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host). When the host stops, Orleans leaves the cluster, closes gateways and networking, deactivates grains, and stops providers in reverse lifecycle order.

## Let the Generic Host own shutdown

Use `RunAsync` or `RunConsoleAsync` and don't terminate the process directly:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    // Configure Orleans.
});

await builder.Build().RunAsync();
```

The console lifetime handles <kbd>Ctrl</kbd>+<kbd>C</kbd>, `SIGINT`, and `SIGTERM`. ASP.NET Core hosts use the same host lifetime model. For details, see [.NET Generic Host shutdown](https://learn.microsoft.com/dotnet/core/extensions/generic-host#host-shutdown).

In tests or embedded hosts, call `StopAsync` and dispose the host:

```csharp
await host.StopAsync(cancellationToken);
host.Dispose();
```

Don't call `Environment.Exit`, kill the process from application code, or dispose Orleans services independently of the host.

## Configure a shutdown budget

The host cancellation token bounds every hosted service, including Orleans lifecycle participants and grain deactivation. Configure <xref:Microsoft.Extensions.Hosting.HostOptions.ShutdownTimeout> for the expected workload:

```csharp
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(45);
});
```

Set the orchestrator's termination grace period longer than the host shutdown timeout. Include time for:

- Load balancers and readiness probes to stop sending new traffic.
- Gateway and membership changes to propagate.
- Grain deactivation callbacks and state writes.
- Stream, reminder, storage, and telemetry providers to flush and stop.

If the external grace period expires first, the process is killed and graceful shutdown can't complete.

## Make application code shutdown-safe

- Honor cancellation tokens in hosted services, startup tasks, lifecycle participants, and provider calls.
- Keep `OnDeactivateAsync` bounded; persist important state during normal operation rather than relying only on shutdown.
- Stop accepting new application work before the termination deadline.
- Make recovery safe after abrupt termination, because crashes and node loss remain possible.
- Avoid synchronous blocking and unbounded retries in shutdown callbacks.

Grains can move or reactivate elsewhere after a silo leaves. Don't use graceful shutdown as an application-wide drain barrier unless the application separately coordinates that behavior.

## Containers and orchestrators

Configure readiness to fail before sending the termination signal when the platform supports a pre-stop phase. Send a normal termination signal, allow the host timeout to elapse, and reserve forceful termination for hung processes.

For ordered Orleans callbacks, see [Orleans silo lifecycle](../silo-lifecycle.md).
