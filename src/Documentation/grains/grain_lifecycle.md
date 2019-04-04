---
layout: page
title: Grain Lifecycle
---

# Grain Lifecycle

## Overview

Orleans grains use an observable lifecycle (See [Orleans Lifecycle](../implementation/orleans_lifecycle.md)) for ordered activation and deactivation.
This allows grain logic, system components and application logic to be started and stopped in an ordered manner during grain activation and collection.

### Stages

The pre-defined grain lifecycle stages are as follows.

```csharp
public static class GrainLifecycleStage
{
    public const int First = int.MinValue;
    public const int SetupState = 1000;
    public const int Activate = 2000;
    public const int Last = int.MaxValue;
}
```

- `First` - First stage in grain’s lifecycle
- `SetupState` – Setup grain state, prior to activation. For stateful grains, this is the stage where state is loaded from storage.
- `Activate` – Stage where `OnActivateAsync` and `OnDeactivateAsync` are called
- `Last` - Last stage in grain's lifecycle

While the grain lifecycle will be used during grain activation, since grains are not always deactivated during some error cases (such as silo crashes), applications should not rely on the grain lifecycle always being executed during grain deactivations.

### Grain Lifecycle Participation
Application logic can participate with a grain’s lifecycle in two ways:
The grain can participate in its lifecycle, and/or components can access the lifecycle via the grain activation context (see IGrainActivationContext.ObservableLifecycle).

A grain always participates in its own lifecycle, so application logic can be introduced by overriding the participate method.

### Example

```csharp
public override void Participate(IGrainLifecycle lifecycle)
{
    base.Participate(lifecycle);
    lifecycle.Subscribe(this.GetType().FullName, GrainLifecycleStage.SetupState, OnSetupState);
}
```

In the above example, `Grain<T>` overrides the `Participate` method to tell the lifecycle to call its OnSetupState method during the SetupState stage of the lifecycle.

Components created during a grain’s construction can take part in the lifecycle as well, without any special grain logic being added.
Since the grain’s activation context (`IGrainActivationContext`), including the grain’s lifecycle (`IGrainActivationContext.ObservableLifecycle`), is created before the grain is created, any component injected into the grain by the container can participate in the grain’s lifecycle.

### Example

The below component participates in the grain’s lifecycle when created using its factory function `Create(..)`.
This logic could exist in the component’s constructor, but that risks the component being added to the lifecycle before it’s fully constructed, which may not be safe.

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

By registering the above component in the service container using its `Create(..)` factory function, any grain constructed with the component as a dependency will have the component taking part in its lifecycle without any special logic in the grain.

#### Register component in container

```csharp
    services.AddTransient<MyComponent>(sp =>
        MyComponent.Create(sp.GetRequiredService<IGrainActivationContext>());
```

#### Grain with component as dependency

```csharp
public class MyGrain : Grain, IMyGrain
{
    private readonly MyComponent component;

    public MyGrain(MyComponent component)
    {
        this.component = component;
    }
}
```
