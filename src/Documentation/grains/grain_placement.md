---
layout: page
title: Grain Placement
---

# Grain Placement

When a grain is activated in Orleans, the runtime decides which server (silo) to activate that grain on.
This is called grain placement.
The placement process in Orleans is fully configurable: developers can choose from a set of out-of-the-box placement policies such as random, prefer-local, and load-based, or custom logic can be configured.
This allows for full flexibility in deciding where grains are created.
For example, grains can be placed on a server close to resources which they need to operate on or other grains which they communicate with.

## Sample custom placement strategy

First define a class which implements `IPlacementDirector` interface, requiring a single method.
In this example we assume you have a function `GetSiloNumber` defined which will return a silo number given the guid of the grain about to be created.

``` csharp
public class SamplePlacementStrategyFixedSiloDirector : IPlacementDirector
{

    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var silos = context.GetCompatibleSilos(target).OrderBy(s => s).ToArray();
        int silo = GetSiloNumber(target.GrainIdentity.PrimaryKey, silos.Length);
        return Task.FromResult(silos[silo]);
    }
}
```
You then need to define two classes to allow grain classes to be assigned to the strategy:

```csharp
[Serializable]
public class SamplePlacementStrategy : PlacementStrategy
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SamplePlacementStrategyAttribute : PlacementAttribute
{
    public SamplePlacementStrategyAttribute() :
        base(new SamplePlacementStrategy())
        {
        }
}
```
Then just tag any grain classes you want to be using this strategy with the attribute:
``` csharp
[SamplePlacementStrategy]
public class MyGrain : Grain, IMyGrain
{
    ...
}
```
And finally register the strategy when you build the SiloHost:
``` csharp
private static async Task<ISiloHost> StartSilo()
{
    ISiloHostBuilder builder = new SiloHostBuilder()
        // normal configuration methods omitted for brevity
        .ConfigureServices(ConfigureServices);

    var host = builder.Build();
    await host.StartAsync();
    return host;
}


private static void ConfigureServices(IServiceCollection services)
{
    services.AddSingletonNamedService<PlacementStrategy, SamplePlacementStrategy>(nameof(SamplePlacementStrategy));
    services.AddSingletonKeyedService<Type, IPlacementDirector, SamplePlacementStrategyFixedSiloDirector>(typeof(SamplePlacementStrategy));
}
```

For a second simple example, showing further use of the placement context, refer to the `PreferLocalPlacementDirector` in the [Orleans source repo](https://github.com/dotnet/orleans/blob/master/src/Orleans.Runtime/Placement/PreferLocalPlacementDirector.cs)
