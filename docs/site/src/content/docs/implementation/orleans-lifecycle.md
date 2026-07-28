---
title: Orleans lifecycle
description: Learn the various lifecycles of .NET Orleans apps.
ms.date: 03/30/2025
ms.topic: concept-article
---

# Orleans lifecycle overview

Some Orleans behaviors are sufficiently complex that they require ordered startup and shutdown. Components with such behaviors include grains, silos, and clients. To address this, Orleans introduced a general component lifecycle pattern. This pattern consists of an observable lifecycle, responsible for signaling stages of a component's startup and shutdown, and lifecycle observers, responsible for performing startup or shutdown operations at specific stages.

For more information, see [Grain lifecycle](../grains/grain-lifecycle.md) and [Silo lifecycle](../host/silo-lifecycle.md).

## Observable lifecycle

Components needing ordered startup and shutdown can use an observable lifecycle. This allows other components to observe the lifecycle and receive notifications when a specific stage is reached during startup or shutdown.

```csharp
public interface ILifecycleObservable
{
    IDisposable Subscribe(
        string observerName,
        int stage,
        ILifecycleObserver observer);
}
```

The subscribe call registers an observer for notification when a stage is reached during startup or shutdown. The observer's name is used for reporting purposes. The stage indicates at which point in the startup/shutdown sequence the observer receives notification. Each lifecycle stage is observable. All observers are notified when the stage is reached during startup and shutdown. Stages start in ascending order and stop in descending order. The observer can unsubscribe by disposing of the returned disposable object.

## Lifecycle observer

Components needing to participate in another component's lifecycle must provide hooks for their startup and shutdown behaviors and subscribe to a specific stage of an observable lifecycle.

```csharp
public interface ILifecycleObserver
{
    Task OnStart(CancellationToken ct);
    Task OnStop(CancellationToken ct);
}
```

Both <xref:Orleans.ILifecycleObserver.OnStart*?displayProperty=nameWithType> and <xref:Orleans.ILifecycleObserver.OnStop*?displayProperty=nameWithType> are called when the subscribed stage is reached during startup or shutdown.

## Utilities

For convenience, helper functions exist for common lifecycle usage patterns.

### Extensions

Extension functions are available for subscribing to an observable lifecycle that don't require the subscribing component to implement `ILifecycleObserver`. Instead, these allow components to pass in lambdas or member functions to be called at the subscribed stages.

```csharp
IDisposable Subscribe(
    this ILifecycleObservable observable,
    string observerName,
    int stage,
    Func<CancellationToken, Task> onStart,
    Func<CancellationToken, Task> onStop);

IDisposable Subscribe(
    this ILifecycleObservable observable,
    string observerName,
    int stage,
    Func<CancellationToken, Task> onStart);
```

Similar extension functions allow using generic type arguments instead of the observer name.

```csharp
IDisposable Subscribe<TObserver>(
    this ILifecycleObservable observable,
    int stage,
    Func<CancellationToken, Task> onStart,
    Func<CancellationToken, Task> onStop);

IDisposable Subscribe<TObserver>(
    this ILifecycleObservable observable,
    int stage,
    Func<CancellationToken, Task> onStart);
```

### Lifecycle participation

Some extensibility points need a way to recognize which components are interested in participating in a lifecycle. A lifecycle participant marker interface serves this purpose. More details on its usage are covered when exploring silo and grain lifecycles.

```csharp
public interface ILifecycleParticipant<TLifecycleObservable>
    where TLifecycleObservable : ILifecycleObservable
{
    void Participate(TLifecycleObservable lifecycle);
}
```

## Example

From the Orleans lifecycle tests, below is an example of a component that participates in an observable lifecycle at multiple stages.

```csharp
enum TestStages
{
    Down,
    Initialize,
    Configure,
    Run,
};

class MultiStageObserver : ILifecycleParticipant<ILifecycleObservable>
{
    public Dictionary<TestStages,bool> Started { get; } = new();
    public Dictionary<TestStages, bool> Stopped { get; } = new();

    private Task OnStartStage(TestStages stage)
    {
        Started[stage] = true;

        return Task.CompletedTask;
    }

    private Task OnStopStage(TestStages stage)
    {
        Stopped[stage] = true;

        return Task.CompletedTask;
    }

    public void Participate(ILifecycleObservable lifecycle)
    {
        lifecycle.Subscribe<MultiStageObserver>(
            (int)TestStages.Down,
            _ => OnStartStage(TestStages.Down),
            _ => OnStopStage(TestStages.Down));

        lifecycle.Subscribe<MultiStageObserver>(
            (int)TestStages.Initialize,
            _ => OnStartStage(TestStages.Initialize),
            _ => OnStopStage(TestStages.Initialize));

        lifecycle.Subscribe<MultiStageObserver>(
            (int)TestStages.Configure,
            _ => OnStartStage(TestStages.Configure),
            _ => OnStopStage(TestStages.Configure));

        lifecycle.Subscribe<MultiStageObserver>(
            (int)TestStages.Run,
            _ => OnStartStage(TestStages.Run),
            _ => OnStopStage(TestStages.Run));
    }
}
```
