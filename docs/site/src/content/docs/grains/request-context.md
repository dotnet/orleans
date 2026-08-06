---
title: Request context
description: Flow application metadata with Orleans grain calls.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Request context

<xref:Orleans.Runtime.RequestContext> carries application metadata with an Orleans request. Typical values include correlation IDs, tenant IDs, and authorization context established by trusted application code.

```csharp
RequestContext.Set("trace-id", Guid.NewGuid().ToString("N"));

IOrderGrain order = grainFactory.GetGrain<IOrderGrain>("order-42");
await order.Submit();
```

The receiving grain reads the value:

```csharp
public sealed class OrderGrain(
    ILogger<OrderGrain> logger) : Grain, IOrderGrain
{
    public Task Submit()
    {
        string? traceId = RequestContext.Get("trace-id") as string;
        logger.LogInformation(
            "Submitting order with trace ID {TraceId}",
            traceId);

        return Task.CompletedTask;
    }
}
```

Values must be serializable by Orleans. Keep them small because Orleans includes them in request messages.

## Propagation

Request context uses async-local storage. When code sends a grain call, Orleans copies the current entries into the outgoing request. The receiving grain sees those entries, and calls it makes propagate its current context onward.

Changes made by a callee don't flow back in the response. Treat request context as downstream metadata, not as a return channel.

Use the static API to manage entries:

```csharp
object? value = RequestContext.Get("tenant-id");
RequestContext.Set("tenant-id", "tenant-17");
bool removed = RequestContext.Remove("tenant-id");
RequestContext.Clear();
```

Set context as close as possible to the operation that needs it, and restore or clear values before unrelated operations execute in the same asynchronous flow.

## Security

Request context is caller-provided data. Don't trust a role, user ID, or tenant ID merely because it arrived in `RequestContext`. Establish authentication at a trusted boundary and use call filters or application authorization logic to validate access.

## Placement and migration

Placement occurs before a new activation exists, so the static `RequestContext` isn't populated inside placement directors and filters. Read <xref:Orleans.Runtime.Placement.PlacementTarget.RequestContextData> instead.

When a grain requests migration using `MigrateOnIdle()`, Orleans captures the current request context and makes it available to placement. This allows an application to provide placement hints, but custom placement logic remains an advanced runtime extension.

## Call-chain reentrancy

<xref:Orleans.Runtime.RequestContext.AllowCallChainReentrancy> and `SuppressCallChainReentrancy` use request metadata internally to control scheduling for a call chain. Use their scoped return values with `using`; don't set Orleans-reserved context keys directly. See [Request scheduling](request-scheduling.md#call-chain-reentrancy).
