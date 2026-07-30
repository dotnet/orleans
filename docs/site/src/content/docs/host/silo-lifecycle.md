---
title: Orleans silo lifecycles
description: Learn about .NET Orleans silo lifecycles.
ms.date: 01/22/2026
ms.topic: overview
---

# Orleans silo lifecycle overview

Orleans silos use an observable lifecycle for the ordered startup and shutdown of Orleans systems and application layer components. For more information on the implementation details, see [Orleans lifecycle](../implementation/orleans-lifecycle.md).

## Stages

Orleans silo and cluster clients use a common set of service lifecycle stages:

```csharp
public static class ServiceLifecycleStage
{
    public const int First = int.MinValue;
    public const int RuntimeInitialize = 2_000;
    public const int RuntimeServices = 4_000;
    public const int RuntimeStorageServices = 6_000;
    public const int RuntimeGrainServices = 8_000;
    public const int ApplicationServices = 10_000;
    public const int BecomeActive = Active - 1;
    public const int Active = 20_000;
    public const int Last = int.MaxValue;
}
```

- <xref:Orleans.ServiceLifecycleStage.First?displayProperty=nameWithType>: The first stage in the service's lifecycle.
- <xref:Orleans.ServiceLifecycleStage.RuntimeInitialize?displayProperty=nameWithType>: Initialization of the runtime environment, where the silo initializes threading.
- <xref:Orleans.ServiceLifecycleStage.RuntimeServices?displayProperty=nameWithType>: Start of runtime services, where the silo initializes networking and various agents.
- <xref:Orleans.ServiceLifecycleStage.RuntimeStorageServices?displayProperty=nameWithType>: Initialization of runtime storage.
- <xref:Orleans.ServiceLifecycleStage.RuntimeGrainServices?displayProperty=nameWithType>: Starting of runtime services for grains. This includes grain type management, membership service, and grain directory.
- <xref:Orleans.ServiceLifecycleStage.ApplicationServices?displayProperty=nameWithType>: Application layer services.
- <xref:Orleans.ServiceLifecycleStage.BecomeActive?displayProperty=nameWithType>: The silo joins the cluster.
- <xref:Orleans.ServiceLifecycleStage.Active?displayProperty=nameWithType>: The silo is active in the cluster and ready to accept workload.
- <xref:Orleans.ServiceLifecycleStage.Last?displayProperty=nameWithType>: The last stage in the service's lifecycle.

## Logging

Due to the inversion of control, where participants join the lifecycle rather than the lifecycle having a centralized set of initialization steps, it's not always clear from the code what the startup/shutdown order is. To help address this, Orleans adds logging before silo startup to report which components participate at each stage. These logs are recorded at the _Information_ log level on the <xref:Orleans.Runtime.SiloLifecycleSubject?displayProperty=fullName> logger. For instance:

```Output
Information, Orleans.Runtime.SiloLifecycleSubject, "Stage 2000: Orleans.Statistics.PerfCounterEnvironmentStatistics, Orleans.Runtime.InsideRuntimeClient, Orleans.Runtime.Silo"

Information, Orleans.Runtime.SiloLifecycleSubject, "Stage 4000: Orleans.Runtime.Silo"

Information, Orleans.Runtime.SiloLifecycleSubject, "Stage 10000: Orleans.Runtime.Versions.GrainVersionStore, Orleans.Storage.AzureTableGrainStorage-Default, Orleans.Storage.AzureTableGrainStorage-PubSubStore"
```

Additionally, Orleans similarly logs timing and error information for each component by stage. For instance:

```Output
Information, Orleans.Runtime.SiloLifecycleSubject, "Lifecycle observer Orleans.Runtime.InsideRuntimeClient started in stage 2000 which took 33 Milliseconds."

Information, Orleans.Runtime.SiloLifecycleSubject, "Lifecycle observer Orleans.Statistics.PerfCounterEnvironmentStatistics started in stage 2000 which took 17 Milliseconds."
```

## Silo lifecycle participation

Your application logic can participate in the silo's lifecycle by registering a participating service in the silo's service container. Register the service as an <xref:Orleans.ILifecycleParticipant`1>, where `T` is <xref:Orleans.Runtime.ISiloLifecycle>.

```csharp
public interface ISiloLifecycle : ILifecycleObservable
{
}

public interface ILifecycleParticipant<TLifecycleObservable>
    where TLifecycleObservable : ILifecycleObservable
{
    void Participate(TLifecycleObservable lifecycle);
}
```

When the silo starts, all participants (`ILifecycleParticipant<ISiloLifecycle>`) in the container can participate by having their <xref:Orleans.ILifecycleParticipant`1.Participate*?displayProperty=nameWithType> behavior called. Once all have had the opportunity to participate, the silo's observable lifecycle starts all stages in order.

### Example

With the introduction of the silo lifecycle, bootstrap providers, which previously allowed you to inject logic at the provider initialization phase, are no longer necessary. You can now inject application logic at any stage of silo startup. Nonetheless, we added a 'startup task' facade to aid the transition for developers who used bootstrap providers. As an example of how you can develop components that participate in the silo's lifecycle, let's look at the startup task facade.

The startup task only needs to inherit from `ILifecycleParticipant<ISiloLifecycle>` and subscribe the application logic to the silo lifecycle at the specified stage.

```csharp
class StartupTask : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Func<IServiceProvider, CancellationToken, Task> _startupTask;
    private readonly int _stage;

    public StartupTask(
        IServiceProvider serviceProvider,
        Func<IServiceProvider, CancellationToken, Task> startupTask,
        int stage)
    {
        _serviceProvider = serviceProvider;
        _startupTask = startupTask;
        _stage = stage;
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe<StartupTask>(
            _stage,
            cancellation => _startupTask(_serviceProvider, cancellation));
    }
}
```

From the preceding implementation, you can see that in the `Participate(...)` call, it subscribes to the silo lifecycle at the configured stage, passing the application callback rather than its initialization logic. Components needing initialization at a given stage would provide their callback, but the pattern remains the same. Now that you have a `StartupTask` ensuring the application's hook is called at the configured stage, you need to ensure the `StartupTask` participates in the silo lifecycle.

For this, register it in the container using the <xref:Orleans.Hosting.SiloBuilderStartupExtensions.AddStartupTask*> extension method on the silo builder:

```csharp
siloBuilder.AddStartupTask(
    async (serviceProvider, cancellationToken) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Silo is starting up...");

        // Perform initialization logic, such as warming caches or validating configuration
        var config = serviceProvider.GetRequiredService<IConfiguration>();
        await ValidateExternalDependenciesAsync(config, cancellationToken);
    },
    ServiceLifecycleStage.Active);
```

By registering the `StartupTask` in the silo's service container as the marker interface `ILifecycleParticipant<ISiloLifecycle>`, you signal to the silo that this component needs to participate in the silo lifecycle.
