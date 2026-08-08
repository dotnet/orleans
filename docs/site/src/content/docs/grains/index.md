---
title: Develop Orleans grains
description: Define grain contracts, implement grains, and call them in Orleans.
ms.date: 08/07/2026
ms.topic: article
---

# Develop Orleans grains

A grain is an application object with a stable logical identity. Orleans activates it on demand, routes calls to its current activation, and removes idle activations from memory. Application code works with grain references instead of constructing grain classes or locating activations.

Projects defining grain contracts or implementations reference [Microsoft.Orleans.Sdk](https://www.nuget.org/packages/Microsoft.Orleans.Sdk). For host and project setup, see [Create a multi-project Orleans application](../tutorials-and-samples/multi-project-orleans-application.md#create-the-solution).

## Define a grain contract

A grain interface derives from one of the grain key interfaces and declares asynchronous methods:

```csharp
public interface IShoppingCartGrain : IGrainWithStringKey
{
    ValueTask AddItem(CartItem item);

    ValueTask<IReadOnlyList<CartItem>> GetItems();

    Task Checkout();

    Task<Receipt> GetReceipt();
}

[GenerateSerializer]
public sealed record CartItem(
    [property: Id(0)] string ProductId,
    [property: Id(1)] int Quantity);

[GenerateSerializer]
public sealed record Receipt(
    [property: Id(0)] string OrderId);
```

Orleans supports these grain method return types:

- <xref:System.Threading.Tasks.Task>
- <xref:System.Threading.Tasks.Task`1>
- <xref:System.Threading.Tasks.ValueTask>
- <xref:System.Threading.Tasks.ValueTask`1>
- <xref:System.Collections.Generic.IAsyncEnumerable`1>

Use <xref:System.Threading.Tasks.Task> or <xref:System.Threading.Tasks.ValueTask> for methods without a result, and their generic forms for methods returning a result. Use <xref:System.Collections.Generic.IAsyncEnumerable`1> for [response streaming](response-streaming.md). Don't use `void`, `async void`, or synchronous return types in grain contracts. A <xref:System.Threading.CancellationToken> can be included as a method parameter for cooperative cancellation. For the underlying C# model, see [Asynchronous programming](https://learn.microsoft.com/dotnet/csharp/asynchronous-programming/).

Arguments, return values, and exceptions cross process boundaries. Make application data serializable by Orleans, normally using <xref:Orleans.GenerateSerializerAttribute> and stable <xref:Orleans.IdAttribute> values. Grain references are already serializable and can be passed in calls or stored as part of grain state.

## Implement the contract

A grain class usually derives from <xref:Orleans.Grain> and implements one or more grain interfaces:

```csharp
public sealed class ShoppingCartGrain : Grain, IShoppingCartGrain
{
    private readonly List<CartItem> _items = [];

    public ValueTask AddItem(CartItem item)
    {
        _items.Add(item);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<CartItem>> GetItems() =>
        ValueTask.FromResult<IReadOnlyList<CartItem>>(_items.ToArray());

    public Task Checkout() => Task.CompletedTask;

    public Task<Receipt> GetReceipt() =>
        Task.FromResult(new Receipt($"order-{this.GetPrimaryKeyString()}"));
}
```

Orleans creates grain classes through dependency injection. Constructor injection is available for application services, and <xref:Orleans.IGrainFactory> is available through <xref:Orleans.Grain.GrainFactory?displayProperty=nameWithType>.

> [!IMPORTANT]
> A grain activation processes one turn at a time. Don't block on tasks using <xref:System.Threading.Tasks.Task`1.Result>, <xref:System.Threading.Tasks.Task.Wait*>, or `GetAwaiter().GetResult()`. Use `await` so Orleans can resume the request on the activation scheduler.

## Get and call a grain reference

Use <xref:Orleans.IGrainFactory.GetGrain*> with the interface and key:

```csharp
IShoppingCartGrain cart =
    grainFactory.GetGrain<IShoppingCartGrain>("customer-42");

await cart.AddItem(new CartItem("SKU-123", 2));
IReadOnlyList<CartItem> items = await cart.GetItems();
```

Getting a reference doesn't create or activate a grain. The first call that needs an activation causes Orleans to place and activate it. The reference remains valid if the activation moves, deactivates, or is recreated on another silo.

## Understand call completion

A regular grain call completes in one of these ways:

- The method returns successfully, optionally with a result.
- The method throws, and the exception is propagated to the caller.
- The caller cancels the call and observes <xref:System.OperationCanceledException>.
- The caller doesn't receive a response before its response timeout and observes <xref:System.TimeoutException>.
- Messaging or cluster failures prevent the call from completing.

A timeout tells the caller that no response arrived in time. It doesn't prove that the grain method didn't run or won't finish. <xref:Orleans.Configuration.MessagingOptions.CancelRequestOnTimeout> defaults to `false`; when enabled, Orleans sends a best-effort cancellation signal after a timeout, and the grain must still cooperate by observing a cancellation token.

Distributed calls can be retried by application code or infrastructure after an uncertain outcome. Design operations to be idempotent when duplicate execution would be harmful. A common pattern is to include an operation ID and persist completed IDs with the state change.

Configure a per-method timeout on the interface:

```csharp
public interface IReportGrain : IGrainWithGuidKey
{
    [ResponseTimeout("00:00:10")]
    Task<Report> Generate(CancellationToken cancellationToken = default);
}
```

Global defaults are configured through <xref:Orleans.Configuration.ClientMessagingOptions> and <xref:Orleans.Configuration.SiloMessagingOptions>. See [client configuration](../host/configuration-guide/client-configuration.md), [server configuration](../host/configuration-guide/server-configuration.md), and [cancellation tokens](cancellation-tokens.md).

## Activation and deactivation

Override the current lifecycle methods when a grain needs activation-scoped setup or cleanup:

```csharp
public override Task OnActivateAsync(CancellationToken cancellationToken)
{
    return base.OnActivateAsync(cancellationToken);
}

public override Task OnDeactivateAsync(
    DeactivationReason reason,
    CancellationToken cancellationToken)
{
    return base.OnDeactivateAsync(reason, cancellationToken);
}
```

<xref:Orleans.Grain.OnActivateAsync*> accepts a <xref:System.Threading.CancellationToken>; there is no parameterless overload. Deactivation callbacks are best effort and don't run after process termination or some failures, so don't rely on them to persist critical state.

See [Grain lifecycle](grain-lifecycle.md) for collection, lifecycle participation, and migration.

## Choose basic or advanced features

Most grains only need a contract, an implementation, a stable key, and regular request-response calls. Add specialized behavior only when the workload requires it:

- [Request scheduling and reentrancy](request-scheduling.md)
- [Response streaming with IAsyncEnumerable](response-streaming.md)
- [Timers and reminders](timers-and-reminders.md)
- [Observers](observers.md)
- [Grain placement](grain-placement.md)
- [Stateless worker grains](stateless-worker-grains.md)
- [Grain call filters](interceptors.md)
- [Grain extensions](grain-extensions.md)
- [Grain services](grainservices.md)

The [Orleans runtime implementation](../implementation/index.md) documentation describes internal scheduling, messaging, and lifecycle components. Those details aren't required for basic grain development.
