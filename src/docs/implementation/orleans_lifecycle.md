---
layout: page
title: Orleans Lifecycle
---

# Orleans Lifecycle

## Overview

Some Orleans behaviors are sufficiently complex that they need ordered startup and shutdown.
Some components with such behaviors include grains, silos, and clients.
To address this, a general component lifecycle pattern has been introduced.
This pattern consists of an observable lifecycle, which is responsible for signaling on stages of a component’s startup and shutdown, and lifecycle observers which are responsible for performing startup or shutdown operations at specific stages.

See also [Grain Lifecycle](~/docs/grains/grain_lifecycle.md) and [Silo Lifecycle](~/docs/clusters_and_clients/silo_lifecycle.md).

## Observable Lifecycle

Components that need ordered startup and shutdown can use an observable lifecycle which allows other components to observe the LiveCycle and receive notification when a stage is reached during startup or shutdown.

```csharp
    public interface ILifecycleObservable
    {
        IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer);
    }
```

The subscribe call registers an observer for notification when a stage is reached while starting or stopping.  The observer name is for reporting purposes.  The stage indicated at which point in the startup/shutdown sequence the observer will be notified.  Each stage of lifecycle is observable.  All observers will be notified when the stage is reached when starting and stopping.  Stages are started in ascending order and stopped in descending order.  The observer can unsubscribe by disposing of the returned disposable.

## Lifecycle Observer

Components which need to take part in another component’s lifecycle need provide hooks for their startup and shutdown behaviors and subscribe to a specific stage of an observable lifecycle.

```csharp
    public interface ILifecycleObserver
    {
        Task OnStart(CancellationToken ct);
        Task OnStop(CancellationToken ct);
    }
```

`OnStart/OnStop` will be called when the stage subscribed to is reached during startup/shutdown.

## Utilities

For convenience, helper functions have been created for common lifecycle usage patterns.

### Extensions

Extension functions exist for subscribing to observable lifecycle which do not require that the subscribing component implement ILifecycleObserver.  Instead, these allow components to pass in lambdas or members function to be called at the subscribed stages.

```csharp
IDisposable Subscribe(this ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop);

IDisposable Subscribe(this ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart);
```

Similar extension functions allow generic type arguments to be used in place of the observer name.

```csharp
IDisposable Subscribe<TObserver>(this ILifecycleObservable observable, int stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop);

IDisposable Subscribe<TObserver>(this ILifecycleObservable observable, int stage, Func<CancellationToken, Task> onStart);
```

### Lifecycle Participation

Some extensibility points need a way of recognizing what components are interested in participating in a lifecycle.  A lifecycle participant marker interface has been introduced for this purpose.  More about how this is used will be covered when exploring silo and grain lifecycles.

```csharp
    public interface ILifecycleParticipant<TLifecycleObservable>
        where TLifecycleObservable : ILifecycleObservable
    {
        void Participate(TLifecycleObservable lifecycle);
    }
```

## Example
From our lifecycle tests, below is an example of a component that takes part in an observable lifecycle at multiple stages of the lifecycle.

```csharp
enum TestStages
{
    Down,
    Initialize,
    Configure,
    Run,
}

class MultiStageObserver : ILifecycleParticipant<ILifecycleObservable>
{
    public Dictionary<TestStages,bool> Started { get; } = new Dictionary<TestStages, bool>();
    public Dictionary<TestStages, bool> Stopped { get; } = new Dictionary<TestStages, bool>();

    private Task OnStartStage(TestStages stage)
    {
        this.Started[stage] = true;
        return Task.CompletedTask;
    }

    private Task OnStopStage(TestStages stage)
    {
        this.Stopped[stage] = true;
        return Task.CompletedTask;
    }

    public void Participate(ILifecycleObservable lifecycle)
    {
        lifecycle.Subscribe<MultiStageObserver>((int)TestStages.Down, ct => OnStartStage(TestStages.Down), ct => OnStopStage(TestStages.Down));
        lifecycle.Subscribe<MultiStageObserver>((int)TestStages.Initialize, ct => OnStartStage(TestStages.Initialize), ct => OnStopStage(TestStages.Initialize));
        lifecycle.Subscribe<MultiStageObserver>((int)TestStages.Configure, ct => OnStartStage(TestStages.Configure), ct => OnStopStage(TestStages.Configure));
        lifecycle.Subscribe<MultiStageObserver>((int)TestStages.Run, ct => OnStartStage(TestStages.Run), ct => OnStopStage(TestStages.Run));
    }
}
```

