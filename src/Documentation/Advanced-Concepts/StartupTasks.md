---
layout: page
title: Startup Tasks
---
# Startup Tasks

In many cases, some task needs to be performed automatically as soon as a silo becomes available. *Startup Tasks* provide this functionality.

Some use cases include, but are not limited to:

* Starting background timers to perform periodic housekeeping tasks
* Pre-loading some cache grains with data downloaded from external backing storage

Any exceptions that are thrown from a startup task during startup will be reported in the silo log and will stop the silo.

This fail-fast approach is the standard way that Orleans handles silo start-up issues, and is intended to allow any problems with silo configuration and/or bootstrap logic to be easily detected during testing phases rather than being silently ignored and causing unexpected problems later in the silo lifecycle.

## Configuring Startup Tasks

Startup tasks can be configured using the `ISiloHostBuilder` either by registering a delegate to be invoked during startup or by registering a implementation of `IStartupTask`.

### Example: Registering a delegate

``` csharp
siloHostBuilder.AddStartupTask(
  async (IServiceProvider services, CancellationToken cancellation) =>
  {
    // Use the service provider to get the grain factory.
    var grainFactory = services.GetRequiredService<IGrainFactory>();

    // Get a reference to a grain and call a method on it.
    var grain = grainFactory.GetGrain<IMyGrain>(0);
    await grain.Initialize();
});
```

### Example: Registering an `IStartupTask` implementation

First we must define an implementation of `IStartupTask`:

``` csharp
public class CallGrainStartupTask : IStartupTask
{
    private readonly IGrainFactory grainFactory;

    public CallGrainStartupTask(IGrainFactory grainFactory)
    {
        this.grainFactory = grainFactory;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        var grain = this.grainFactory.GetGrain<IMyGrain>(0);
        await grain.Initialize();
    }
}
```

Then that implementation must be registered with the `ISiloHostBuilder`:

``` csharp
siloHostBuilder.AddStartupTask<CallGrainStartupTask>();
```
