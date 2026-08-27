---
title: Grain placement and migration
description: Understand placement, resource-optimized defaults, and activation movement in Orleans.
ms.date: 08/27/2026
ms.topic: concept-article
---

# Grain placement and migration

When a grain isn't active, Orleans selects a compatible silo and creates an activation there. This process is **placement**. Callers continue using location-transparent grain references, so placement doesn't change application call sites.

This article covers application-facing placement configuration. For the runtime algorithms and coordination protocols behind placement and activation movement, see [Placement and activation balancing](../implementation/load-balancing.md).

## What Orleans balances

Placement, collection, migration, and load shedding solve different problems:

| Event or mechanism | What Orleans does |
|---|---|
| A call needs an activation | Runs placement using the current compatible silos and current placement statistics. |
| A silo joins | Includes it in later placement decisions after membership and compatibility information converge. Existing activations continue running on their current silos. |
| An activation remains idle | Collects it after the configured idle period. A later call runs placement again, so the replacement activation can use newly added capacity. |
| A silo leaves or fails | Removes its activations. Calls are routed to activations on remaining silos or cause replacement activations to be placed there. |
| Explicit or automatic migration is requested | Moves an eligible live activation after its current work completes. Cluster-wide automatic migration is experimental and opt-in. |
| Enabled load shedding marks a silo overloaded | The client gateway rejects requests, stream queue flow control pauses reads at its CPU threshold, and resource-optimized placement favors non-overloaded candidates. |

A hosting platform or operator controls the silo count. Orleans adapts placement and routing to the resulting membership.

## Default placement

<xref:Orleans.Runtime.ResourceOptimizedPlacement> is the default placement strategy. It uses sampled silo runtime statistics and a power-of-k-choices algorithm to balance new activations. It considers CPU, memory, available memory, activation count, and a preference for the local silo. When load shedding marks silos overloaded, placement favors non-overloaded candidates.

For design background on its resource scoring and signal smoothing, see [Resource-based placement with cooperative dual-mode Kalman filtering](https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/).

Configure its weights through <xref:Orleans.Configuration.ResourceOptimizedPlacementOptions>:

:::code language="csharp" source="../snippets/compiled/Grains/PlacementSnippets.cs" id="configure_resource_optimized_placement":::
Weights are relative and don't need to total 100. Keep defaults until measurements show a workload-specific reason to change them.

## Scale out and scale in

On scale-out, new silos become candidates for new activations. Long-lived active grains continue running on their current silos. Grain code or an opt-in rebalancing service can request their migration. Idle activation collection gradually makes more grain identities eligible for fresh placement.

On graceful scale-in, the departing silo leaves active membership and deactivates its ordinary activations during shutdown. Subsequent calls reactivate those grain identities on remaining silos. After an abrupt loss, failure detection enables the same replacement placement path. In-flight calls can fail or time out, so callers must follow the application's retry and idempotency policy.

Scale gradually and preserve headroom for reactivation, state reads, cache warming, and temporarily concentrated traffic. See [Capacity planning and scaling](../deployment/capacity-planning.md) and [Graceful shutdown and scale-in](../deployment/upgrades.md#graceful-shutdown-and-scale-in).

## Persistence and movement

Persisted grain state belongs to the grain identity and remains in the configured storage provider across activation lifetimes. With <xref:Orleans.Runtime.IPersistentState`1>, Orleans reads configured state before <xref:Orleans.Grain.OnActivateAsync*> when it creates an ordinary replacement activation. Every silo which can host the grain must be able to reach the configured storage provider.

Live activation migration transfers runtime migration state directly to the target, including the in-memory state held by <xref:Orleans.Runtime.IPersistentState`1>. Application-owned in-memory state which must survive a live move must participate through <xref:Orleans.Runtime.IGrainMigrationParticipant>. Awaited storage writes provide durability across process failure; migration state provides continuity during a live move. See [Grain persistence](grain-persistence/index.md) and [Activation lifecycle and migration](../implementation/activation-lifecycle.md#activation-migration).

## Load shedding

Set <xref:Orleans.Configuration.LoadSheddingOptions.LoadSheddingEnabled> to `true` to activate load shedding. Crossing either the CPU or memory threshold marks the silo as overloaded, enables client-gateway request rejection, and makes resource-optimized placement favor non-overloaded candidates. Stream providers which use `LoadShedQueueFlowController` pause queue reads according to CPU usage.

Configure it on every silo and choose thresholds from measured headroom:

:::code language="csharp" source="../snippets/compiled/Grains/PlacementSnippets.cs" id="configure_load_shedding":::

Use load shedding for admission protection, a hosting-platform autoscaler for cluster capacity, and activation rebalancing or repartitioning for eligible activation movement. Set thresholds below the platform's hard limits, retain headroom for deactivation and recovery work, and monitor rejection rate with CPU, memory, queueing, and latency signals.

Gateway load shedding rejects incoming requests after CPU or memory crosses its threshold. Stream queue flow control uses CPU thresholds to pause reads. [Memory-based activation shedding](../host/configuration-guide/activation-collection.md#enable-memory-based-activation-shedding) deactivates selected activations to reduce process memory.

## Per-grain strategies

Apply a placement attribute to a grain implementation when its requirements differ from the default:

| Attribute | Behavior |
|---|---|
| <xref:Orleans.Placement.ResourceOptimizedPlacementAttribute> | Explicitly selects the default resource-aware strategy. |
| <xref:Orleans.Placement.RandomPlacementAttribute> | Chooses a random compatible silo. |
| <xref:Orleans.Placement.PreferLocalPlacementAttribute> | Uses the local compatible silo when possible. |
| <xref:Orleans.Placement.HashBasedPlacementAttribute> | Maps the grain ID across the current compatible silo set. |
| <xref:Orleans.Placement.ActivationCountBasedPlacementAttribute> | Favors sampled silos with fewer activations. |
| <xref:Orleans.Placement.SiloRoleBasedPlacementAttribute> | Restricts placement by silo role. |
| <xref:Orleans.Concurrency.StatelessWorkerAttribute> | Uses local, scalable [worker-pool placement](stateless-worker-grains.md). |

Activation-count-based placement applies the power-of-two-choices technique described in [The Power of Two Choices in Randomized Load Balancing](https://www.eecs.harvard.edu/~michaelm/postscripts/mythesis.pdf).

:::code language="csharp" source="../snippets/compiled/Grains/PlacementSnippets.cs" id="prefer_local_grain":::
Placement happens when creating an activation. Changing cluster membership or a strategy doesn't move existing activations by itself.

## Direct placement and migration with placement hints

A placement hint directs a new activation or a migrating activation to a specific silo. Set <xref:Orleans.Runtime.Placement.IPlacementDirector.PlacementHintKey> in <xref:Orleans.Runtime.RequestContext> before making the grain call that can trigger activation or before calling <xref:Orleans.Grain.MigrateOnIdle>. The built-in placement directors select the hinted silo when it belongs to the compatible candidate set after version compatibility and placement filters are applied.

The following grain injects <xref:Orleans.Runtime.IClusterMembershipService> and <xref:Orleans.Runtime.ILocalSiloDetails> and selects an active silo other than its own. `ProcessOrder` directs a new worker activation to that silo, while `MoveToAnotherSilo` directs migration of the coordinator activation:

:::code language="csharp" source="snippets/placement/PlacementHints.cs" id="direct_placement_with_hint":::

The membership snapshot provides the silos known to the caller at that point in time. For a new activation, the grain call, rather than `GetGrain`, triggers placement. If the worker is already active, Orleans routes the call to its existing activation. <xref:Orleans.Grain.MigrateOnIdle> captures the current request context and starts migration asynchronously after the current work completes. Migration occurs only when placement selects a different compatible silo.

If membership or compatibility changes remove the hinted silo from the candidate set, the configured placement strategy selects from the current compatible silos. Restore the previous request-context value after each operation because request context propagates to outgoing grain calls.

## Override the cluster default

Register a different default strategy only when all unannotated grains should use it:

:::code language="csharp" source="../snippets/compiled/Grains/PlacementSnippets.cs" id="configure_random_placement":::
Per-grain attributes still take precedence.

## Placement filters

Placement filters reduce the compatible candidate set before the placement strategy selects a silo. They can express requirements or preferences based on [silo metadata](../host/configuration-guide/silo-metadata.md). The built-in metadata filter attributes are experimental and produce diagnostic `ORLEANSEXP004`.

See [Placement filters](grain-placement-filtering.md) for the built-in filters and experimental status.

## Request migration

A grain can ask Orleans to move its activation after current work completes:

:::code language="csharp" source="../snippets/compiled/Grains/PlacementSnippets.cs" id="move_grain":::
Migration is advisory and occurs only if placement chooses another compatible silo. Use a [placement hint](#direct-placement-and-migration-with-placement-hints) to direct the migration to a specific compatible silo. Custom activation state must participate in dehydration and rehydration; see [Grain activation and lifecycle](grain-lifecycle.md#grain-migration).

Use <xref:Orleans.Placement.ImmovableAttribute> to exclude a grain class from automatic movement:

:::code language="csharp" source="../snippets/compiled/Grains/PlacementSnippets.cs" id="immovable_grain":::
The attribute doesn't prevent explicit <xref:Orleans.Grain.MigrateOnIdle> calls.

## Experimental automatic movement

Orleans includes two opt-in experimental services:

| Feature | Goal | Diagnostic |
|---|---|---|
| Activation repartitioner | Improve grain-to-grain call locality. | `ORLEANSEXP001` |
| Activation rebalancer | Balance activation count and memory pressure across silos. | `ORLEANSEXP002` |

Enable them independently:

:::code language="csharp" source="../snippets/compiled/Grains/PlacementSnippets.cs" id="configure_activation_rebalancing":::
Both features migrate eligible activations and can operate together. They add cluster coordination and state-transfer costs, so benchmark representative workloads before production use. Stateless workers, system targets, grain services, client objects, and immovable activations aren't candidates.

Choose the activation rebalancer when uneven activation count or activation memory is the problem. Choose the activation repartitioner when cross-silo calls between grains are the problem. Enabling both lets the repartitioner's default tolerance rule incorporate the rebalancer's view of cluster imbalance. Pair them with a hosting-platform autoscaler for capacity and load shedding for overload admission control. See [Placement and activation balancing](../implementation/load-balancing.md#choosing-the-mechanism) for tuning and observability details.

## Implement custom placement

Custom placement strategies and directors are advanced runtime extensions. Implement them only when built-in strategies plus [placement filters](grain-placement-filtering.md) can't express the requirement. A custom implementation has three parts:

1. A <xref:Orleans.Runtime.PlacementStrategy> that identifies the policy.
1. A <xref:Orleans.Placement.PlacementAttribute> that applies the policy to a grain class.
1. An <xref:Orleans.Runtime.Placement.IPlacementDirector> that selects one candidate silo.

The following example gives related grain types an affinity for the same silo when they use the same grain key. This differs from <xref:Orleans.Runtime.HashBasedPlacement>, which hashes the complete grain ID, including its grain type.

First, define the strategy and its attribute:

:::code language="csharp" source="snippets/placement/CustomPlacement.cs" id="custom_placement_strategy":::

Placement strategy instances can cross runtime serialization boundaries. Use `[GenerateSerializer]`; if the strategy adds serializable state, assign stable `[Id(n)]` values to its members. The strategy in this example is immutable and has no serialized members.

Next, implement the director:

:::code language="csharp" source="snippets/placement/CustomPlacement.cs" id="custom_placement_director":::

Call <xref:Orleans.Runtime.Placement.IPlacementContext.GetCompatibleSilos*> instead of reconstructing cluster membership. It returns active silos which can host the grain type and satisfy interface-version compatibility, after placement filters have run. The current runtime throws if that process leaves no candidates; the explicit empty-set check also protects the modulo operation in tests or alternate context implementations. <xref:Orleans.Runtime.Placement.IPlacementDirector.GetPlacementHint*> accepts a request hint only when it names one of those candidates, so the example honors valid hints before applying its own policy.

The director sorts the candidate addresses before indexing them and uses the grain key's stable, uniform hash. Therefore, two grain types with the same key select the same silo only when they see the same candidate set:

:::code language="csharp" source="snippets/placement/CustomPlacement.cs" id="apply_custom_placement":::

This is an affinity, not durable pinning. Membership changes, silo restarts, or different compatibility and filter results can change the mapping. Existing activations don't move merely because a later placement decision maps elsewhere. The uniform hash is deterministic across cluster nodes but isn't cryptographic, so don't use placement as an authorization or isolation boundary.

Finally, register the strategy and director on every silo:

:::code language="csharp" source="snippets/placement/CustomPlacement.cs" id="register_custom_placement":::

This overload registers the stateless strategy and the director as keyed singletons. Other overloads can change the strategy lifetime, but the director remains a keyed singleton. Directors must therefore be thread-safe and use singleton-safe dependencies. If a strategy carries attribute configuration, preserve it through <xref:Orleans.Runtime.PlacementStrategy.PopulateGrainProperties*> and <xref:Orleans.Runtime.PlacementStrategy.Initialize*> and use a lifetime which doesn't share mutable configuration between grain types.

This example deliberately trades resource-aware balancing for affinity. If load is the primary concern, prefer <xref:Orleans.Runtime.ResourceOptimizedPlacement> or apply a filter and let a built-in placement strategy choose from the remaining candidates. For more implementations, inspect the built-in directors under [`src/Orleans.Runtime/Placement`](https://github.com/dotnet/orleans/tree/main/src/Orleans.Runtime/Placement), including [`HashBasedPlacementDirector`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Placement/HashBasedPlacementDirector.cs).
