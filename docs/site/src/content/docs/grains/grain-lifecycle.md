---
title: Grain lifecycle overview
description: Learn about grain lifecycles in .NET Orleans.
ms.date: 01/21/2026
ms.topic: concept-article
zone_pivot_groups: orleans-version
---

# Grain lifecycle overview

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

Orleans grains use an observable lifecycle (see [Orleans Lifecycle](../implementation/orleans-lifecycle.md)) for ordered activation and deactivation. This allows grain logic, system components, and application logic to start and stop in an ordered manner during grain activation and collection.

:::zone-end

## Stages

The predefined grain lifecycle stages are as follows:

```csharp
public static class GrainLifecycleStage
{
    public const int First = int.MinValue;
    public const int SetupState = 1_000;
    public const int Activate = 2_000;
    public const int Last = int.MaxValue;
}
```

- `First`: First stage in a grain's lifecycle.
- `SetupState`: Set up grain state before activation. For stateful grains, this is the stage where Orleans loads <xref:Orleans.Core.IStorage`1.State?displayProperty=nameWithType> from storage if <xref:Orleans.Core.IStorage.RecordExists?displayProperty=nameWithType> is `true`.
- `Activate`: Stage where Orleans calls <xref:Orleans.Grain.OnActivateAsync*?displayProperty=nameWithType> and <xref:Orleans.Grain.OnDeactivateAsync*?displayProperty=nameWithType>.
- `Last`: Last stage in a grain's lifecycle.

While Orleans uses the grain lifecycle during grain activation, grains aren't always deactivated during some error cases (such as silo crashes). Therefore, applications shouldn't rely on the grain lifecycle always executing during grain deactivations.

:::zone target="docs" pivot="orleans-10-0,orleans-9-0"

## Memory-based activation shedding

Memory-based activation shedding automatically deactivates grain activations when the silo is under memory pressure. This helps prevent out-of-memory conditions by intelligently removing grains from memory based on their activity patterns.

### How it works

When enabled, Orleans monitors memory usage and begins deactivating grains when usage exceeds the configured threshold:

1. Orleans polls memory usage at a configurable interval (default: 5 seconds)
2. When memory usage exceeds `MemoryUsageLimitPercentage`, Orleans begins deactivating grains
3. Orleans prioritizes deactivating older and less-recently-used grains first
4. Deactivation continues until memory usage falls below `MemoryUsageTargetPercentage`

### Configuration

Configure memory-based activation shedding using <xref:Orleans.Configuration.GrainCollectionOptions>:

```csharp
builder.Configure<GrainCollectionOptions>(options =>
{
    // Enable memory-based activation shedding
    options.EnableActivationSheddingOnMemoryPressure = true;

    // Memory usage percentage (0-100) at which grain collection triggers
    options.MemoryUsageLimitPercentage = 80; // default: 80

    // Target memory usage percentage to reach after collection
    options.MemoryUsageTargetPercentage = 75; // default: 75

    // How often to poll memory usage
    options.MemoryUsagePollingPeriod = TimeSpan.FromSeconds(5); // default: 5s
});
```

### GrainCollectionOptions properties

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `EnableActivationSheddingOnMemoryPressure` | `bool` | `false` | Enable automatic deactivation under memory pressure. |
| `MemoryUsageLimitPercentage` | `int` | 80 | Memory usage percentage at which shedding begins. Valid range: 0-100. |
| `MemoryUsageTargetPercentage` | `int` | 75 | Target memory usage percentage after shedding. Valid range: 0-100. |
| `MemoryUsagePollingPeriod` | <xref:System.TimeSpan> | 5 seconds | How often to check memory usage. |
| `CollectionAge` | <xref:System.TimeSpan> | 15 minutes | Minimum time a grain must be idle before eligible for collection. |
| `CollectionQuantum` | <xref:System.TimeSpan> | 1 minute | How often idle grain collection runs. |

### Best practices

- **Set thresholds conservatively**: Leave headroom between `MemoryUsageTargetPercentage` and `MemoryUsageLimitPercentage` to avoid oscillation
- **Monitor collection metrics**: Track grain deactivations to understand the impact on your workload
- **Consider grain importance**: Critical grains can extend their lifetime using timers with `KeepAlive = true` or by receiving periodic calls
- **Test under load**: Verify behavior under production-like memory conditions

:::zone-end

## Grain lifecycle participation

Application logic can participate in a grain's lifecycle in two ways:

:::zone target="docs" pivot="orleans-8-0,orleans-7-0"

- The grain can participate in its own lifecycle.
- Components can access the lifecycle via the grain activation context (see <xref:Orleans.Runtime.IGrainContext.ObservableLifecycle?displayProperty=nameWithType>).

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

- The grain can participate in its own lifecycle.
- Components can access the lifecycle via the grain activation context (see <xref:Orleans.Runtime.IGrainActivationContext.ObservableLifecycle?displayProperty=nameWithType>).

:::zone-end

A grain always participates in its lifecycle, so application logic can be introduced by overriding the participate method.

### Example participation

```csharp
public override void Participate(IGrainLifecycle lifecycle)
{
    base.Participate(lifecycle);
    lifecycle.Subscribe(
        this.GetType().FullName,
        GrainLifecycleStage.SetupState,
        OnSetupState);
}
```

In the preceding example, <xref:Orleans.Grain`1> overrides the <xref:Orleans.Grain.Participate*?displayProperty=nameWithType> method to tell the lifecycle to call its `OnSetupState` method during the <xref:Orleans.Runtime.GrainLifecycleStage.SetupState?displayProperty=nameWithType> stage of the lifecycle.

:::zone target="docs" pivot="orleans-8-0,orleans-7-0"

Components created during a grain's construction can also participate in the lifecycle without adding any special grain logic. Since Orleans creates the grain's context (<xref:Orleans.Runtime.IGrainContext>), including its lifecycle (<xref:Orleans.Runtime.IGrainContext.ObservableLifecycle?displayProperty=nameWithType>), before creating the grain, any component injected into the grain by the container can participate in the grain's lifecycle.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Components created during a grain's construction can also participate in the lifecycle without adding any special grain logic. Since Orleans creates the grain's activation context (<xref:Orleans.Runtime.IGrainActivationContext>), including its lifecycle (<xref:Orleans.Runtime.IGrainActivationContext.ObservableLifecycle?displayProperty=nameWithType>), before creating the grain, any component injected into the grain by the container can participate in the grain's lifecycle.

:::zone-end

### Example: Component participation

The following component participates in the grain's lifecycle when created using its factory function `Create(...)`. This logic could exist in the component's constructor, but that risks adding the component to the lifecycle before it's fully constructed, which might not be safe.

:::zone target="docs" pivot="orleans-8-0,orleans-7-0"

```csharp
public class MyComponent : ILifecycleParticipant<IGrainLifecycle>
{
    public static MyComponent Create(IGrainContext context)
    {
        var component = new MyComponent();
        component.Participate(context.ObservableLifecycle);
        return component;
    }

    public void Participate(IGrainLifecycle lifecycle)
    {
        lifecycle.Subscribe<MyComponent>(GrainLifecycleStage.Activate, OnActivate);
    }

    private Task OnActivate(CancellationToken ct)
    {
        // Do stuff
    }
}
```

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

```csharp
public class MyComponent : ILifecycleParticipant<IGrainLifecycle>
{
    public static MyComponent Create(IGrainActivationContext context)
    {
        var component = new MyComponent();
        component.Participate(context.ObservableLifecycle);
        return component;
    }

    public void Participate(IGrainLifecycle lifecycle)
    {
        lifecycle.Subscribe<MyComponent>(GrainLifecycleStage.Activate, OnActivate);
    }

    private Task OnActivate(CancellationToken ct)
    {
        // Do stuff
    }
}
```

:::zone-end

By registering the example component in the service container using its `Create(...)` factory function, any grain constructed with the component as a dependency has the component participate in its lifecycle without requiring any special logic in the grain itself.

#### Register component in container

:::zone target="docs" pivot="orleans-8-0,orleans-7-0"

```csharp
services.AddTransient<MyComponent>(sp =>
    MyComponent.Create(sp.GetRequiredService<IGrainContext>());
```

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

```csharp
services.AddTransient<MyComponent>(sp =>
    MyComponent.Create(sp.GetRequiredService<IGrainActivationContext>());
```

:::zone-end

#### Grain with component as a dependency

```csharp
public class MyGrain : Grain, IMyGrain
{
    private readonly MyComponent _component;

    public MyGrain(MyComponent component)
    {
        _component = component;
    }
}
```

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

## Grain migration

Grain migration allows grain activations to move from one silo to another while preserving their in-memory state. This enables load balancing and cluster optimization without losing grain state. The migration process involves:

1. **Dehydration**: Serializing the grain's state on the source silo before deactivation
2. **Transfer**: Sending the serialized state to the target silo
3. **Rehydration**: Restoring the grain's state on the new activation before `OnActivateAsync` is called

### When migration occurs

Migration can occur in several scenarios:

- **Application-initiated**: A grain can request migration by calling `this.MigrateOnIdle()`
- **Activation repartitioner**: Automatically collocates frequently communicating grains to optimize call locality (experimental)
- **Activation rebalancer**: Distributes activations to balance load across silos (experimental)

### Implementing migration support

Grains can participate in migration by implementing <xref:Orleans.Runtime.IGrainMigrationParticipant>:

```csharp
public interface IGrainMigrationParticipant
{
    void OnDehydrate(IDehydrationContext dehydrationContext);
    void OnRehydrate(IRehydrationContext rehydrationContext);
}
```

#### Option 1: Implement IGrainMigrationParticipant directly

For grains with custom in-memory state that should be preserved during migration:

```csharp
public class MyMigratableGrain : Grain, IMyGrain, IGrainMigrationParticipant
{
    private int _cachedValue;
    private string _sessionData;

    public void OnDehydrate(IDehydrationContext dehydrationContext)
    {
        // Save state before migration
        dehydrationContext.TryAddValue("cached", _cachedValue);
        dehydrationContext.TryAddValue("session", _sessionData);
    }

    public void OnRehydrate(IRehydrationContext rehydrationContext)
    {
        // Restore state after migration
        rehydrationContext.TryGetValue("cached", out _cachedValue);
        rehydrationContext.TryGetValue("session", out _sessionData);
    }
}
```

#### Option 2: Use Grain&lt;TState&gt; or IPersistentState&lt;T&gt;

Grains using <xref:Orleans.Grain`1> or <xref:Orleans.Runtime.IPersistentState`1> automatically support migration. Their state is serialized and restored without additional code:

```csharp
// Automatic migration support via Grain<TState>
public class MyStatefulGrain : Grain<MyGrainState>, IMyGrain
{
    public Task UpdateValue(int value)
    {
        State.Value = value;
        return WriteStateAsync();
    }
}

// Automatic migration support via IPersistentState<T>
public class MyOtherGrain : Grain, IMyGrain
{
    private readonly IPersistentState<MyGrainState> _state;

    public MyOtherGrain(
        [PersistentState("state", "storage")] IPersistentState<MyGrainState> state)
    {
        _state = state;
    }
}
```

### Triggering migration

A grain can request migration to a new silo:

```csharp
public class MyGrain : Grain, IMyGrain
{
    public Task RequestMigration()
    {
        // Request migration when the grain becomes idle
        this.MigrateOnIdle();
        return Task.CompletedTask;
    }
}
```

You can also specify a preferred target silo using placement hints:

```csharp
public Task MigrateToSilo(SiloAddress targetSilo)
{
    RequestContext.Set(IPlacementDirector.PlacementHintKey, targetSilo);
    this.MigrateOnIdle();
    return Task.CompletedTask;
}
```

### Preventing automatic migration

Use the <xref:Orleans.Placement.ImmovableAttribute> to prevent automatic migration by the repartitioner or rebalancer:

```csharp
// Prevent all automatic migration
[Immovable]
public class MyImmovableGrain : Grain, IMyGrain { }

// Prevent only repartitioner migration
[Immovable(ImmovableKind.Repartitioner)]
public class MyPartiallyImmovableGrain : Grain, IMyGrain { }
```

The `ImmovableKind` enum provides these options:

| Value | Description |
|-------|-------------|
| `Repartitioner` | Won't be migrated by the activation repartitioner |
| `Rebalancer` | Won't be migrated by the activation rebalancer |
| `Any` | Won't be migrated by any automatic process (default) |

> [!NOTE]
> The `[Immovable]` attribute only prevents *automatic* migration. User-initiated migration via `MigrateOnIdle()` still works.

### Non-migratable grain types

The following grain types cannot be migrated:

- Client grains
- System targets
- Grain services
- Stateless worker grains

:::zone-end
