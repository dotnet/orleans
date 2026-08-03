---
title: Placement and activation balancing
description: Understand Orleans resource-optimized placement, load signals, activation rebalancing, and repartitioning.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Placement and activation balancing

Placement chooses a silo when Orleans needs a new activation. Balancing can later move existing activations. Those are separate decisions with different information and costs.

The default placement strategy is <xref:Orleans.Runtime.ResourceOptimizedPlacement>, not random placement.

## Resource-optimized placement

`ResourceOptimizedPlacementDirector` receives cluster-wide `SiloRuntimeStatistics` from `DeploymentLoadPublisher`. It excludes incompatible and overloaded silos, samples approximately the square root of the available candidates, normalizes their signals, adds jitter to avoid deterministic herding, and selects the lowest utilization score.

```mermaid
flowchart LR
    Stats[SiloRuntimeStatistics]
    Publisher[DeploymentLoadPublisher]
    Director[ResourceOptimizedPlacementDirector]
    Candidates[Compatible, non-overloaded silos]
    Score[Weighted normalized score]
    Target[Selected silo]

    Stats --> Publisher
    Publisher --> Director
    Candidates --> Director
    Director --> Score
    Score --> Target
```

The default relative weights are:

| Signal | Weight |
| --- | ---: |
| CPU usage | 40 |
| Memory usage | 20 |
| Available memory | 20 |
| Maximum available memory | 5 |
| Activation count | 15 |

`LocalSiloPreferenceMargin` defaults to 5. If the local silo's score is within that margin of the best candidate, placement can preserve locality. If statistics are not yet available, the director falls back to a random compatible silo.

Source: [`ResourceOptimizedPlacementDirector`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Placement/ResourceOptimizedPlacementDirector.cs), [`ResourceOptimizedPlacementOptions`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Configuration/Options/ResourceOptimizedPlacementOptions.cs), and the default registration in [`DefaultSiloServices`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Hosting/DefaultSiloServices.cs#L218-L241).

## Placement resolution and extension

`PlacementStrategyResolver` selects a grain-specific strategy when one is declared; otherwise it uses the default. `PlacementService` applies placement filters before calling the strategy's keyed <xref:Orleans.Runtime.IPlacementDirector>.

A custom strategy consists of:

1. a `PlacementStrategy` value associated with the grain type;
1. an `IPlacementDirector` which chooses from compatible silos; and
1. registration through `AddPlacementDirector<TStrategy, TDirector>()`.

Placement filters are orthogonal constraints. They can remove candidates based on metadata or another policy before the director scores them. A director should use the candidates supplied by the placement context instead of reconstructing cluster membership.

Source: [`PlacementService`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Placement/PlacementService.cs), [`PlacementStrategyResolver`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Placement/PlacementStrategyResolver.cs), and [`PlacementStrategyExtensions`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Hosting/PlacementStrategyExtensions.cs).

## Why initial placement is not enough

Resource-optimized placement only affects new activations. Long-lived activations can become unbalanced after:

- a silo joins or leaves;
- traffic changes while the activation remains alive;
- activation memory use diverges;
- a placement constraint changes the candidate set; or
- communicating grains are spread across silos.

Moving an activation has a cost: dehydrate and rehydrate work, directory updates, cold caches, and a temporary interruption. Orleans therefore exposes opt-in protocols rather than continuously moving every activation.

## Experimental activation rebalancer

`AddActivationRebalancer()` enables the resource rebalancer and produces compiler warning **`ORLEANSEXP002`**. A worker observes cluster statistics in sessions, estimates imbalance using entropy, and asks source silos to migrate random activations toward underloaded silos. A monitor can wake or relocate the worker after failure.

The protocol optimizes distribution of activation count and memory use. It does not inspect the communication graph, so a more balanced cluster can still have cross-silo hot paths.

Source: [`ActivationRebalancerWorker`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Placement/Rebalancing/ActivationRebalancerWorker.cs) and [`ActivationRebalancerOptions`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Configuration/Options/ActivationRebalancerOptions.cs).

## Experimental activation repartitioner

`AddActivationRepartitioner()` enables communication-aware repartitioning and produces compiler warning **`ORLEANSEXP001`**. The repartitioner samples fully addressed request messages and constructs a weighted graph:

- vertices represent migratable activations;
- edge weights represent observed calls;
- anchored or non-migratable grains constrain possible moves.

Periodically, peer repartitioners exchange graph information and negotiate activation moves which improve locality while respecting an imbalance-tolerance rule. The default `RebalancerCompatibleRule` can incorporate the resource rebalancer's cluster-imbalance report.

Source: [`ActivationRepartitioner`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Placement/Repartitioning/ActivationRepartitioner.cs), [`RepartitionerMessageFilter`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Placement/Repartitioning/RepartitionerMessageFilter.cs), and [`ActivationRepartitionerOptions`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Configuration/Options/ActivationRepartitionerOptions.cs).

## Choosing the mechanism

| Mechanism | When it acts | Primary signal | Movement |
| --- | --- | --- | --- |
| Resource-optimized placement | Activation creation | CPU, memory, capacity, activation count | None |
| Activation rebalancer | Opt-in balancing sessions | Cluster resource imbalance | Random eligible activations |
| Activation repartitioner | Opt-in exchange rounds | Grain call graph and tolerance rule | Communication-aware activations |

The experimental protocols use [activation migration](activation-lifecycle.md). Neither replaces capacity planning, admission control, or deployment health monitoring. Operational guidance belongs in the [deployment section](../deployment/index.md).
