---
title: Background services and startup tasks
description: Run application initialization and background work with Orleans.
ms.date: 08/02/2026
ms.topic: how-to
---

# Background services and startup tasks

Use standard [.NET hosted services](https://learn.microsoft.com/dotnet/core/extensions/workers) for application initialization and background work. Orleans participates in the same Generic Host, so registration order can ensure Orleans is ready before a hosted service starts.

## Run continuous background work

Register Orleans first, then the `BackgroundService`:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    // Configure Orleans.
});

builder.Services.AddHostedService<GrainPingService>();

await builder.Build().RunAsync();
```

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="background_service":::

Honor `stoppingToken` so the service doesn't delay silo shutdown. Use `IHostedService` directly for one-time startup and shutdown work.

## Run a required Orleans startup task

Use `AddStartupTask` when initialization must complete at an Orleans lifecycle stage before startup can continue:

```csharp
siloBuilder.AddStartupTask(
    async (services, cancellationToken) =>
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();
        var grain = grainFactory.GetGrain<IInitializerGrain>("application");
        await grain.Initialize(cancellationToken);
    },
    ServiceLifecycleStage.Active);
```

The default stage is `ServiceLifecycleStage.Active`. An exception fails silo startup. This is appropriate for mandatory validation or initialization, but not for optional work that can retry after the host becomes ready.

For reusable tasks, implement `IStartupTask`:

```csharp
public sealed class ValidateDependenciesTask : IStartupTask
{
    private readonly IDependencyValidator _validator;

    public ValidateDependenciesTask(IDependencyValidator validator)
    {
        _validator = validator;
    }

    public Task Execute(CancellationToken cancellationToken) =>
        _validator.ValidateAsync(cancellationToken);
}
```

```csharp
siloBuilder.AddStartupTask<ValidateDependenciesTask>(
    ServiceLifecycleStage.ApplicationServices);
```

## Choose the right mechanism

| Requirement | Mechanism |
|---|---|
| Continuous loop or scheduled application work | `BackgroundService` |
| One-time host startup and shutdown work | `IHostedService` |
| Mandatory initialization at a specific Orleans stage | `AddStartupTask` |
| Start and stop callbacks integrated with an Orleans subsystem | `ILifecycleParticipant<ISiloLifecycle>` |

Don't use a startup task for long-running loops, database migrations that multiple replicas could race to apply, or work that needs unbounded retries. Coordinate migrations externally or make them safely single-writer.

See [Orleans silo lifecycle](../silo-lifecycle.md) for lifecycle stage selection.
