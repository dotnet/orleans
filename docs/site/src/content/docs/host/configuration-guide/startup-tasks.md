---
title: Background Services and Startup Tasks
description: Learn how to configure and manage background services and startup tasks in .NET Orleans.
ms.date: 05/23/2025
---

# Background services and startup tasks

When building Orleans applications, you often need to perform background operations or initialize components when the application starts.

Use startup tasks to perform initialization work when a silo starts, either before or after it begins accepting requests. Common use cases include:

- Initializing grain state or preloading data
- Setting up external service connections
- Performing database migrations
- Validating configuration
- Warming up caches

## Using `BackgroundService` (Recommended)

The recommended approach is to use .NET [`BackgroundService` or `IHostedService`](/aspnet/core/fundamentals/host/hosted-services). See the [Background tasks with hosted services in ASP.NET Core](/aspnet/core/fundamentals/host/hosted-services) documentation for more information.

Here's an example that pings a grain every 5 seconds:

```csharp
public class GrainPingService : BackgroundService
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<GrainPingService> _logger;

    public GrainPingService(
        IGrainFactory grainFactory,
        ILogger<GrainPingService> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Pinging grain...");
                    var grain = _grainFactory.GetGrain<IMyGrain>("ping-target");
                    await grain.Ping();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Log the error but continue running
                    _logger.LogError(ex, "Failed to ping grain. Will retry in 5 seconds.");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ignore cancellation during shutdown.
        }
        finally
        {
            _logger.LogInformation("Grain ping service is shutting down.");
        }
    }
}
```

Registration order is significant because services added to the host builder start one by one in the order they are registered. Register the background service as follows:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Orleans first
builder.UseOrleans(siloBuilder => 
{
    // Orleans configuration...
});

// Register the background service after calling 'UseOrleans' to make it start once Orleans has started.
builder.Services.AddHostedService<GrainPingService>();

var app = builder.Build();
```

The background service starts automatically when the application starts and gracefully shuts down when the application stops.

## Using `IHostedService`

For simpler scenarios where you don't need continuous background operation, implement `IHostedService` directly:

```csharp
public class GrainInitializerService : IHostedService
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<GrainInitializerService> _logger;

    public GrainInitializerService(
        IGrainFactory grainFactory,
        ILogger<GrainInitializerService> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing grains...");
        var grain = _grainFactory.GetGrain<IMyGrain>("initializer");
        await grain.Initialize();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

Register it the same way:

```csharp
builder.Services.AddHostedService<GrainInitializerService>();
```

## Orleans startup tasks

> [!NOTE]
> While startup tasks are still supported, we recommend using `BackgroundService` or `IHostedService` instead, as they are the standard .NET hosting mechanism for running background tasks.

> [!WARNING]
> Any exceptions thrown from a startup task are reported in the silo log and stop the silo. This fail-fast approach helps detect configuration and bootstrap issues during testing rather than causing unexpected problems later. However, it can also mean that transient failures in a startup task cause host unavailability.

If you need to use the built-in startup task system, configure them as follows:

### Register a delegate

Register a delegate as a startup task using the appropriate <xref:Orleans.Hosting.SiloBuilderStartupExtensions.AddStartupTask*> extension method on <xref:Orleans.Hosting.ISiloBuilder>.

```csharp
siloBuilder.AddStartupTask(
    async (IServiceProvider services, CancellationToken cancellation) =>
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();
        var grain = grainFactory.GetGrain<IMyGrain>("startup-task-grain");
        await grain.Initialize();
    });
```

### Register an `IStartupTask` implementation

Implement the <xref:Orleans.Runtime.IStartupTask> interface and register it as a startup task using the <xref:Orleans.Hosting.SiloBuilderStartupExtensions.AddStartupTask*> extension method on <xref:Orleans.Hosting.ISiloBuilder>.

```csharp
public class CallGrainStartupTask : IStartupTask
{
    private readonly IGrainFactory _grainFactory;

    public CallGrainStartupTask(IGrainFactory grainFactory) =>
        _grainFactory = grainFactory;

    public async Task Execute(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IMyGrain>("startup-task-grain");
        await grain.Initialize();
    }
}
```

Register the startup task as follows:

```csharp
siloBuilder.AddStartupTask<CallGrainStartupTask>();
```
