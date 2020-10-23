---
layout: page
title: Silo Lifecycle
---

# Silo Lifecycle

# Overview

Orleans silo uses an observable lifecycle (See [Orleans Lifecycle](~/docs/implementation/orleans_lifecycle.md)) for ordered startup and shutdown of Orleans systems as well as application layer components.

## Stages

Orleans Silo and Cluster Client use a common set of service lifecycle stages.

```csharp
public static class ServiceLifecycleStage
{
    public const int First = int.MinValue;
    public const int RuntimeInitialize = 2000;
    public const int RuntimeServices = 4000;
    public const int RuntimeStorageServices = 6000;
    public const int RuntimeGrainServices = 8000;
    public const int ApplicationServices = 10000;
    public const int BecomeActive = Active-1;
    public const int Active = 20000;
    public const int Last = int.MaxValue;
}
```

- First - First stage in service's lifecycle
- RuntimeInitialize - Initialize runtime environment.  Silo initializes threading.
- RuntimeServices - Start runtime services.  Silo initializes networking and various agents.
- RuntimeStorageServices - Initialize runtime storage.
- RuntimeGrainServices - Start runtime services for grains.  This includes grain type management, membership service, and grain directory.
- ApplicationServices – Application layer services.
- BecomeActive – Silo joins the cluster.
- Active – Silo is active in the cluster and ready to accept workload.
- Last - Last stage in service's lifecycle

## Logging

Due to the inversion of control, where participants join the lifecycle rather than the lifecycle having some centralized set of initialization steps, it’s not always clear from the code what the startup/shutdown order is.
To help address this, logging has been added prior to silo startup to report what components are participating at each stage.
These logs are recorded at Information log level on the `Orleans.Runtime.SiloLifecycleSubject` logger.  For instance:

_Information, Orleans.Runtime.SiloLifecycleSubject, “Stage 2000: Orleans.Statistics.PerfCounterEnvironmentStatistics, Orleans.Runtime.InsideRuntimeClient, Orleans.Runtime.Silo”_

_Information, Orleans.Runtime.SiloLifecycleSubject, “Stage 4000: Orleans.Runtime.Silo”_

_Information, Orleans.Runtime.SiloLifecycleSubject, “Stage 10000: Orleans.Runtime.Versions.GrainVersionStore, Orleans.Storage.AzureTableGrainStorage-Default, Orleans.Storage.AzureTableGrainStorage-PubSubStore”_

Additionally, timing and error information are similarly logged for each component by stage.  For instance:

_Information, Orleans.Runtime.SiloLifecycleSubject, “Lifecycle observer Orleans.Runtime.InsideRuntimeClient started in stage 2000 which took 33 Milliseconds.”_

_Information, Orleans.Runtime.SiloLifecycleSubject, “Lifecycle observer Orleans.Statistics.PerfCounterEnvironmentStatistics started in stage 2000 which took 17 Milliseconds.”_

## Silo Lifecycle Participation

Application logic can take part in the silo’s lifecycle by registering a participating service in the silo’s service container.  The service must be registered as an ILifecycleParticipant<ISiloLifecycle>.

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

Upon silo start, all participants (`ILifecycleParticipant<ISiloLifecycle>`) in the container will be given an opportunity to participate by calling their `Participate(..)` behavior.
Once all have had the opportunity to participate, the silo’s observable lifecycle will start all stages in order.

## Example

With the introduction of the silo lifecycle, bootstrap providers, which used to allow application developers to inject logic at the provider initialization phase, are no longer necessary, since application logic can now be injected at any stage of silo startup.
Nonetheless, we added a ‘startup task’ façade to aid the transition for developers who had been using bootstrap providers.
As an example of how components can be developed which take part in the silo’s lifecycle, we’ll look at the startup task façade.

The startup task needs only to inherit from `ILifecycleParticipant<ISiloLifecycle>` and subscribe the application logic to the silo lifecycle at the specified stage.

```csharp
class StartupTask : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IServiceProvider serviceProvider;
    private readonly Func<IServiceProvider, CancellationToken, Task> startupTask;
    private readonly int stage;

    public StartupTask(
        IServiceProvider serviceProvider,
        Func<IServiceProvider, CancellationToken, Task> startupTask,
        int stage)
    {
        this.serviceProvider = serviceProvider;
        this.startupTask = startupTask;
        this.stage = stage;
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe<StartupTask>(
            this.stage,
            cancellation => this.startupTask(this.serviceProvider, cancellation));
    }
}
```

From the above implementation, we can see that in the StartupTask’s `Participate(..)` call it subscribes to the silo lifecycle at the configured stage, passing the application callback rather than its own initialization logic.

Components that need to be initialized at a given stage would provide their own callback, but the pattern is the same.

Now that we have a StartupTask which will ensure that the application’s hook is called at the configured stage, we need to ensure that the StartupTask participates in the silo lifecycle.
For this we need only register it in the container.

We do this with an extension function on the SiloHost builder.

```csharp
public static ISiloHostBuilder AddStartupTask(
    this ISiloHostBuilder builder,
    Func<IServiceProvider, CancellationToken, Task> startupTask,
    int stage = ServiceLifecycleStage.Active)
{
    builder.ConfigureServices(services =>
        services.AddTransient<ILifecycleParticipant<ISiloLifecycle>>(sp =>
            new StartupTask(
                sp,
                startupTask,
                stage)));
    return builder;
}
```

By registering the StartupTask in the silo’s service container as the marker interface `ILifecycleParticipant<ISiloLifecycle>`, this signals to the silo that this component needs to take part in the silo lifecycle.
