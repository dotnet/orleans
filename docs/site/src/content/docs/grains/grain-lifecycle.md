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

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="activate_grain":::

<xref:Orleans.Grain.OnActivateAsync*> accepts a <xref:System.Threading.CancellationToken>; there is no parameterless overload. If activation fails, Orleans doesn't make that activation available for calls.

Avoid doing unnecessary work during activation. Activations can be recreated after collection, migration, silo restart, or failure.

## Deactivation

Orleans can deactivate an activation because it has been idle, the silo is stopping, the application requested deactivation, migration is occurring, or an error made the activation invalid.

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="deactivate_grain":::

Deactivation is best effort. <xref:Orleans.Grain.OnDeactivateAsync*> doesn't run if the process terminates abruptly or in some failure cases. Persist important state as part of the operation that changes it, not only during deactivation.

## Influence activation lifetime

Call <xref:Orleans.Grain.DeactivateOnIdle> to ask Orleans to deactivate the grain after the current request and queued work complete:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="deactivate_on_idle":::

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

Components that need ordered activation-scoped behavior can implement <xref:Orleans.ILifecycleParticipant`1> for <xref:Orleans.Runtime.IGrainLifecycle> and subscribe through <xref:Orleans.Runtime.IGrainContext.ObservableLifecycle>. Use distinct stages when one component's startup depends on another completing. Callbacks within a stage can execute concurrently.

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="lifecycle_participant":::

The runtime calls `Participate` on a grain object which implements the participant interface. Services use an explicit enrollment owner, such as a facet factory or the shared activation setup described below.

### Shared activation setup

Use <xref:Orleans.Runtime.IConfigureGrainTypeComponents> to select features for a grain implementation class and register reusable setup actions with <xref:Orleans.Runtime.GrainTypeSharedContext.AddActivationSetup*>. This example selects classes implementing an application-owned `ICachedGrain` marker:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="activation_setup":::

Register the configurator as a singleton and the feature state as scoped:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="activation_setup_registration":::

Orleans caches the selected setup actions in the shared grain type context. Each activation runs those actions in registration order after its grain constructor completes and <xref:Orleans.Runtime.IGrainContext.GrainInstance> is assigned. All setup actions finish before the runtime calls the grain object's `Participate` method and starts lifecycle callbacks. Each stateless worker activation runs the same shared setup with its own context.

The shared action resolves `CacheParticipant` only for selected grains. Resolution uses the activation scope, so constructor injection of `CacheParticipant` and setup share the same scoped service. For interface injection, register an alias factory which resolves that concrete service. Ordinary concrete, keyed, and participant-interface DI registrations retain their explicit enrollment behavior.

Setup actions can run concurrently for different activations. Keep shared actions stateless, or make captured shared data safe for concurrent access; keep activation-specific state in the activation scope. Add actions during shared type configuration. Use synchronous setup to enroll services and lifecycle callbacks for asynchronous initialization and shutdown. Assign one enrollment owner to each feature so that subscriptions are established once.

A setup exception fails the activation, skips remaining setup actions and lifecycle startup, and triggers grain and activation-scope disposal. A fresh activation resolves fresh scoped state and runs the cached setup again.

For the runtime lifecycle model shared by silos and grain activations, see [Orleans runtime lifecycle](../implementation/orleans-lifecycle.md).

## Grain migration

Migration moves an activation to another silo while preserving migration-participating in-memory state. Call <xref:Orleans.Grain.MigrateOnIdle> to request migration after the activation finishes its current work:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="migrate_on_idle":::

The request is advisory. Migration occurs only if placement selects another compatible silo. Orleans carries the current <xref:Orleans.Runtime.RequestContext> into the placement decision.

Implement <xref:Orleans.Runtime.IGrainMigrationParticipant> for custom activation state that must survive migration:

:::code language="csharp" source="../snippets/compiled/Grains/GrainSnippets.cs" id="migration_participant":::

Persistent-state components supplied by Orleans participate automatically. Migration isn't a replacement for durable storage: migrated state is still lost if the source process fails before transfer completes.

Automatic activation repartitioning and rebalancing use migration to improve locality or cluster balance. Both are experimental. See [Grain placement](grain-placement.md) for their status and configuration.

Use <xref:Orleans.Placement.ImmovableAttribute> to exclude a grain type from automatic migration. It doesn't block an explicit <xref:Orleans.Grain.MigrateOnIdle> request.

For the runtime protocols behind activation, collection, deactivation, and migration, see [Activation lifecycle and migration](../implementation/activation-lifecycle.md). Application code should continue to rely on the public lifecycle APIs described here rather than runtime internals.
