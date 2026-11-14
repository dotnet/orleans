---
title: Migrate placement extensions to async filtering
description: Update custom placement filters and directors for asynchronous candidate evaluation.
ms.topic: how-to
---

# Migrate placement extensions to async filtering

Asynchronous placement filtering replaces two synchronous public extension points. This is a source and binary breaking change: rebuild custom filter/director libraries and update context implementations and test doubles together with the runtime. The examples of the new contracts below compile against repository source; released-package examples illustrate the previous contracts.

## Update custom filters

Previously, a custom <xref:Orleans.Placement.IPlacementFilterDirector> implemented `Filter` and returned an enumerable:

:::code language="csharp" source="../grains/snippets/placement/CustomPlacementFilter.cs" id="custom_placement_filter_director":::

Implement <xref:Orleans.Placement.IPlacementFilterDirector.FilterAsync*> instead. Both its input and completed result are materialized read-only lists. The method returns a task and accepts a cancellation token:

:::code language="csharp" source="../snippets/compiled/Grains/CustomPlacementFilter.cs" id="custom_placement_filter_director":::

A synchronous implementation can return <xref:System.Threading.Tasks.Task.FromResult*>. If the filter awaits policy data, pass the supplied token to that operation and materialize the result before completing. Leave the input unchanged, return only candidates from it, and don't modify the result after completion. Returning the input unchanged is a valid pass-through result. Exceptions propagate rather than restoring the unfiltered candidates.

Filter directors remain keyed singletons. Keep operation state local and ensure injected dependencies support concurrent use. The strategy, attribute, registration, manifest configuration, and filter order remain unchanged.

## Update placement directors and test contexts

Previously, a custom director retrieved candidates synchronously:

:::code language="csharp" source="../grains/snippets/placement/CustomPlacement.cs" id="custom_placement_director":::

Await <xref:Orleans.Runtime.Placement.IPlacementContext.GetCompatibleSilosAsync*> instead:

:::code language="csharp" source="../snippets/compiled/Grains/CustomPlacement.cs" id="custom_placement_director":::

<xref:Orleans.Runtime.Placement.IPlacementDirector.OnAddActivation*> retains its existing task-returning signature. The runtime now supplies a context bound to that placement operation, rather than the placement service singleton. Don't cast it to a concrete runtime type or retain it for use after the operation. Reuse a candidate result for fallback selection instead of executing the filter chain again.

Update custom <xref:Orleans.Runtime.Placement.IPlacementContext> implementations and mocks to return candidate tasks. No synchronous adapter is retained. <xref:Orleans.Runtime.Placement.IPlacementContext.GetCompatibleSilosWithVersions*> remains synchronous and returns unfiltered compatibility data.

## Cancellation and operational behavior

The operation-bound context carries message placement timeout/shutdown cancellation. An explicitly supplied query token is linked to it. Migration destination selection carries caller/shutdown cancellation without adding the message-placement retry pipeline.

Candidates use the compatibility snapshot captured before filtering. Filters run sequentially in configured order, and tracing includes asynchronous waiting and result snapshotting. The no-filter path retains compatibility-cache reuse.

Prefer locally cached policy data. Remote dependencies increase activation latency, concurrent outstanding work, and retry load. Keep filters read-only/idempotent and avoid recursive dependencies on grains whose placement uses the same policy. Cancellation stops waiting but cannot forcibly cancel a dependency which ignores its token. Existing activation routing is not a per-call policy check.

See [Grain placement filters](../grains/grain-placement-filtering.md) for the complete contract and operating guidance.
