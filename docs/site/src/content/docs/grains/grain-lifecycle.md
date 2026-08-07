---
title: Grain activation and lifecycle
description: Understand grain activation, deactivation, collection, lifecycle participation, and migration in Orleans.
ms.date: 08/07/2026
ms.topic: concept-article
---

# Grain activation and lifecycle

Orleans activates grains on demand and deactivates idle activations to reclaim resources. Activation is an implementation detail of a grain's stable logical identity: callers continue using the same grain reference across activation changes.

## Activation

Orleans creates grain classes through dependency injection, establishes their grain context, loads configured persistent state, and then calls <xref:Orleans.Grain.OnActivateAsync*>.

Override the cancellation-token overload:

```csharp
public sealed class DeviceGrain(
    IDeviceConnectionFactory connectionFactory) : Grain, IDeviceGrain
{
    private IDeviceConnection? _connection;

    public override async Task OnActivateAsync(
        CancellationToken cancellationToken)
    {
        _connection = await connectionFactory.ConnectAsync(
            this.GetPrimaryKeyString(),
            cancellationToken);

        await base.OnActivateAsync(cancellationToken);
    }
}
```

<xref:Orleans.Grain.OnActivateAsync*> accepts a <xref:System.Threading.CancellationToken>; there is no parameterless overload. If activation fails, Orleans doesn't make that activation available for calls.

Avoid doing unnecessary work during activation. Activations can be recreated after collection, migration, silo restart, or failure.

## Deactivation

Orleans can deactivate an activation because it has been idle, the silo is stopping, the application requested deactivation, migration is occurring, or an error made the activation invalid.

```csharp
public override async Task OnDeactivateAsync(
    DeactivationReason reason,
    CancellationToken cancellationToken)
{
    if (_connection is not null)
    {
        await _connection.DisposeAsync();
    }

    await base.OnDeactivateAsync(reason, cancellationToken);
}
```

Deactivation is best effort. <xref:Orleans.Grain.OnDeactivateAsync*> doesn't run if the process terminates abruptly or in some failure cases. Persist important state as part of the operation that changes it, not only during deactivation.

## Influence activation lifetime

Call <xref:Orleans.Grain.DeactivateOnIdle> to ask Orleans to deactivate the grain after the current request and queued work complete:

```csharp
public Task Close()
{
    DeactivateOnIdle();
    return Task.CompletedTask;
}
```

Call <xref:Orleans.Grain.DelayDeactivation*> to keep an otherwise idle activation eligible for a specified period. This is a hint, not a durability guarantee; failures and shutdown can still remove the activation.

Grain timers don't keep an activation alive by default. Set <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive?displayProperty=nameWithType> only when timer activity should extend the activation lifetime.

## Lifecycle stages and participants

The grain lifecycle exposes ordered stages:

| Stage | Purpose |
|---|---|
| <xref:Orleans.Runtime.GrainLifecycleStage.First?displayProperty=nameWithType> | Earliest subscription point. |
| <xref:Orleans.Runtime.GrainLifecycleStage.SetupState?displayProperty=nameWithType> | State setup and loading. |
| <xref:Orleans.Runtime.GrainLifecycleStage.Activate?displayProperty=nameWithType> | Grain activation and deactivation callbacks. |
| <xref:Orleans.Runtime.GrainLifecycleStage.Last?displayProperty=nameWithType> | Latest subscription point. |

Components that need ordered activation-scoped behavior can implement <xref:Orleans.ILifecycleParticipant`1> for <xref:Orleans.Runtime.IGrainLifecycle> and subscribe through <xref:Orleans.Runtime.IGrainContext.ObservableLifecycle>. `IGrainActivationContext` has been removed; use <xref:Orleans.Runtime.IGrainContext>.

```csharp
public sealed class CacheParticipant : ILifecycleParticipant<IGrainLifecycle>
{
    public void Participate(IGrainLifecycle lifecycle)
    {
        lifecycle.Subscribe<CacheParticipant>(
            GrainLifecycleStage.Activate,
            OnStart,
            OnStop);
    }

    private Task OnStart(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private Task OnStop(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
```

Lifecycle participation is an advanced integration mechanism. Most grains should use <xref:Orleans.Grain.OnActivateAsync*> and <xref:Orleans.Grain.OnDeactivateAsync*>.

For the runtime lifecycle model shared by silos and grain activations, see [Orleans runtime lifecycle](../implementation/orleans-lifecycle.md).

## Grain migration

Migration moves an activation to another silo while preserving migration-participating in-memory state. Call <xref:Orleans.Grain.MigrateOnIdle> to request migration after the activation finishes its current work:

```csharp
public Task RequestMigration()
{
    MigrateOnIdle();
    return Task.CompletedTask;
}
```

The request is advisory. Migration occurs only if placement selects another compatible silo. Orleans carries the current <xref:Orleans.Runtime.RequestContext> into the placement decision.

Implement <xref:Orleans.Runtime.IGrainMigrationParticipant> for custom activation state that must survive migration:

```csharp
public sealed class SessionGrain :
    Grain,
    ISessionGrain,
    IGrainMigrationParticipant
{
    private int _sequence;

    public void OnDehydrate(IDehydrationContext context)
    {
        context.TryAddValue("sequence", _sequence);
    }

    public void OnRehydrate(IRehydrationContext context)
    {
        context.TryGetValue("sequence", out _sequence);
    }
}
```

Persistent-state components supplied by Orleans participate automatically. Migration isn't a replacement for durable storage: migrated state is still lost if the source process fails before transfer completes.

Automatic activation repartitioning and rebalancing use migration to improve locality or cluster balance. Both are experimental. See [Grain placement](grain-placement.md) for their status and configuration.

Use <xref:Orleans.Placement.ImmovableAttribute> to exclude a grain type from automatic migration. It doesn't block an explicit <xref:Orleans.Grain.MigrateOnIdle> request.

For the runtime protocols behind activation, collection, deactivation, and migration, see [Activation lifecycle and migration](../implementation/activation-lifecycle.md). Application code should continue to rely on the public lifecycle APIs described here rather than runtime internals.
