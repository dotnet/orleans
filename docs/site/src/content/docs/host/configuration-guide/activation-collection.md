---
title: Activation collection
description: Learn about activation collection in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
zone_pivot_groups: orleans-version
---

# Activation collection

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"
This article applies to: ✔️ Orleans 7.x and later versions
:::zone-end
:::zone target="docs" pivot="orleans-3-x"
This article applies to: ✔️ Orleans 3.x and earlier versions
:::zone-end

A *grain activation* is an in-memory instance of a grain class that the Orleans runtime automatically creates on an as-needed basis as a temporary physical embodiment of a grain.

Activation collection is the process of removing unused grain activations from memory. It's conceptually similar to how garbage collection works in .NET. However, activation collection only considers how long a particular grain activation has been idle. Memory usage isn't used as a factor.

## How activation collection works

The general process of activation collection involves the Orleans runtime in a silo periodically scanning for grain activations that haven't been used for the configured period (Collection Age Limit). Once a grain activation has been idle for that long, it gets deactivated. The deactivation process begins with the runtime calling the grain's <xref:Orleans.Grain.OnDeactivateAsync> method and completes by removing references to the grain activation object from all silo data structures, allowing the .NET GC to reclaim the memory.

As a result, without burdening your application code, only recently used grain activations stay in memory. Activations no longer in use are automatically removed, and the runtime reclaims the system resources they used.

**What counts as "being active" for grain activation collection:**

- Receiving a method call.
- Receiving a reminder.
- Receiving an event via streaming.

**What does NOT count as "being active" for grain activation collection:**

- Performing a call (to another grain or an Orleans client).
- Timer events.
- Arbitrary I/O operations or external calls not involving the Orleans framework.

**Collection age limit**

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"
The time after which an idle grain activation becomes subject to collection is called the Collection Age Limit. The default Collection Age Limit is 15 minutes, but you can change it globally or for individual grain classes.
:::zone-end
:::zone target="docs" pivot="orleans-3-x"
The time after which an idle grain activation becomes subject to collection is called the Collection Age Limit. The default Collection Age Limit is 2 hours, but you can change it globally or for individual grain classes.
:::zone-end

## Explicit control of activation collection

### Delay activation collection

A grain activation can delay its collection by calling the <xref:Orleans.Grain.DelayDeactivation*> method:

```csharp
protected void DelayDeactivation(TimeSpan timeSpan)
```

This call ensures this activation isn't deactivated for at least the specified time duration. It takes priority over activation collection settings specified in the configuration but doesn't cancel them. Therefore, this call provides another hook to **delay deactivation beyond what's specified in the activation collection settings**. You can't use this call to expedite activation collection.

A positive `timeSpan` value means "prevent collection of this activation for that time."

A negative `timeSpan` value means "cancel the previous setting of the `DelayDeactivation` call and make this activation behave based on the regular activation collection settings."

**Scenarios:**

1. Activation collection settings specify an age limit of 10 minutes, and the grain calls `DelayDeactivation(TimeSpan.FromMinutes(20))`. This causes the activation not to be collected for at least 20 minutes.

1. Activation collection settings specify an age limit of 10 minutes, and the grain calls `DelayDeactivation(TimeSpan.FromMinutes(5))`. The activation will be collected after 10 minutes if no further calls are made.

1. Activation collection settings specify an age limit of 10 minutes, and the grain calls `DelayDeactivation(TimeSpan.FromMinutes(5))`. After 7 minutes, another call arrives for this grain. The activation will be collected after 17 minutes from time zero if no further calls are made.

1. Activation collection settings specify an age limit of 10 minutes, and the grain calls `DelayDeactivation(TimeSpan.FromMinutes(20))`. After 7 minutes, another call arrives for this grain. The activation will be collected after 20 minutes from time zero if no further calls are made.

`DelayDeactivation` doesn't 100% guarantee the grain activation won't be deactivated before the specified time expires. Certain failure cases might cause 'premature' deactivation of grains. This means `DelayDeactivation` **cannot be used as a means to 'pin' a grain activation in memory forever or to a specific silo**. `DelayDeactivation` is merely an optimization mechanism that can help reduce the aggregate cost of a grain being deactivated and reactivated over time. In most cases, you shouldn't need to use `DelayDeactivation` at all.

### Expedite activation collection

A grain activation can also instruct the runtime to deactivate it the next time it becomes idle by calling the <xref:Orleans.Grain.DeactivateOnIdle> method:

```csharp
protected void DeactivateOnIdle()
```

A grain activation is considered idle if it isn't processing any messages at the moment. If you call <xref:Orleans.GrainBaseExtensions.DeactivateOnIdle*> while a grain is processing a message, it deactivates as soon as the processing of the current message finishes. If any requests are queued for the grain, they'll be forwarded to the next activation.

<xref:Orleans.GrainBaseExtensions.DeactivateOnIdle*> takes priority over any activation collection settings specified in the configuration or `DelayDeactivation`.

> [!NOTE]
> This setting only applies to the specific grain activation from which it was called; it doesn't apply to other activations of this grain type.

## Configuration

Configure activation collection using <xref:Orleans.Configuration.GrainCollectionOptions>:

:::zone target="docs" pivot="orleans-7-0,orleans-8-0,orleans-9-0,orleans-10-0"

```csharp
siloBuilder.Configure<GrainCollectionOptions>(options =>
{
    // Set the value of CollectionAge to 10 minutes for all grain
    options.CollectionAge = TimeSpan.FromMinutes(10);

    // Override the value of CollectionAge to 5 minutes for MyGrainImplementation
    options.ClassSpecificCollectionAge[typeof(MyGrainImplementation).FullName] =
        TimeSpan.FromMinutes(5);
});
```

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

```csharp
mySiloHostBuilder.Configure<GrainCollectionOptions>(options =>
{
    // Set the value of CollectionAge to 10 minutes for all grain
    options.CollectionAge = TimeSpan.FromMinutes(10);

    // Override the value of CollectionAge to 5 minutes for MyGrainImplementation
    options.ClassSpecificCollectionAge[typeof(MyGrainImplementation).FullName] =
        TimeSpan.FromMinutes(5);
});
```

:::zone-end

## Memory-based activation shedding

Memory-based activation shedding automatically deactivates grain activations when memory pressure exceeds configured thresholds. This helps prevent out-of-memory conditions by proactively freeing memory when the silo is under memory pressure.

When enabled, the silo monitors memory usage and begins collecting grain activations when memory usage exceeds the configured limit. Collection continues until memory usage falls below the target percentage.

### Enable memory-based activation shedding

Configure memory-based activation shedding using <xref:Orleans.Configuration.GrainCollectionOptions>:

```csharp
siloBuilder.Configure<GrainCollectionOptions>(options =>
{
    // Enable memory-based activation shedding
    options.EnableActivationSheddingOnMemoryPressure = true;

    // Trigger collection when memory usage exceeds 80% (default)
    options.MemoryUsageLimitPercentage = 80;

    // Stop collection when memory usage drops below 75% (default)
    options.MemoryUsageTargetPercentage = 75;

    // Poll memory usage every 5 seconds (default)
    options.MemoryUsagePollingPeriod = TimeSpan.FromSeconds(5);
});
```

### Configuration options

The following table describes the memory-based activation shedding configuration options:

| Property | Default | Description |
|----------|---------|-------------|
| `EnableActivationSheddingOnMemoryPressure` | `false` | When `true`, enables automatic grain deactivation when memory pressure exceeds the configured limit. |
| `MemoryUsageLimitPercentage` | `80` | The memory usage percentage (0–100) at which grain collection is triggered. Must be greater than 0 and less than or equal to 100. |
| `MemoryUsageTargetPercentage` | `75` | The target memory usage percentage (0–100) to reach after grain collection. Must be greater than 0, less than or equal to 100, and less than `MemoryUsageLimitPercentage`. |
| `MemoryUsagePollingPeriod` | `5 seconds` | The interval at which memory usage is polled. |

### How it works

When memory-based activation shedding is enabled:

1. The silo periodically polls memory usage at the configured interval.
2. When memory usage exceeds `MemoryUsageLimitPercentage`, the silo begins deactivating idle grain activations.
3. Grains are deactivated starting with the least recently used activations.
4. Collection continues until memory usage drops below `MemoryUsageTargetPercentage`.
5. The standard grain lifecycle methods (<xref:Orleans.Grain.OnDeactivateAsync*>) are called during deactivation.

This feature works in conjunction with standard activation collection. Grains that have called `DelayDeactivation` or have the `[KeepAlive]` attribute are still respected, though they may be deactivated if memory pressure is severe enough.

## Keep alive

To keep a grain alive indefinitely, apply the <xref:Orleans.KeepAliveAttribute?displayProperty=fullName> to the grain implementation. The `KeepAlive` attribute instructs the Orleans runtime to avoid collecting the grain by the idle activation collector. Avoiding collection is useful for grains used infrequently but that you want to keep alive to avoid potential creation overhead upon the next invocation.

```csharp
public interface IPlayerGrain : IGrainWithGuidKey
{
    Task<IGameGrain> GetCurrentGame();
    Task JoinGame(IGameGrain game);
    Task LeaveGame(IGameGrain game);
}

[KeepAlive]
public class PlayerGrain : Grain, IPlayerGrain
{
    private IGameGrain _currentGame;

    public Task<IGameGrain> GetCurrentGame()
    {
       return Task.FromResult(_currentGame);
    }

    public Task JoinGame(IGameGrain game)
    {
       // Omitted for brevity.

       return Task.CompletedTask;
    }

   public Task LeaveGame(IGameGrain game)
   {
       // Omitted for brevity.

       return Task.CompletedTask;
   }
}
```

The preceding code prevents the idle activation collector from collecting the `PlayerGrain`.
