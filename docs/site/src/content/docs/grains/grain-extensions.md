---
title: Grain extensions
description: Learn how to extend an Orleans Grain.
ms.date: 03/31/2025
ms.topic: concept-article
---

# Grain extensions

Grain extensions provide a way to add extra behavior to grains. By extending a grain with an interface deriving from <xref:Orleans.Runtime.IGrainExtension>, new methods and functionality can be added to the grain.

This article presents two examples of grain extensions. The first example shows how to add a `Deactivate` method to all grains, usable for deactivating the grain. The second example shows how to add `GetState` and `SetState` methods to any grain, allowing manipulation of the grain's internal state.

## Deactivate extension example

This example demonstrates adding a `Deactivate` method to all grains automatically. Use the method to deactivate the grain; it accepts a string as a message parameter. Orleans grains already support this functionality via the <xref:Orleans.Core.Internal.IGrainManagementExtension> interface. Nevertheless, this example shows how this or similar functionality could be added.

### Deactivate extension interface

Start by defining an `IGrainDeactivateExtension` interface containing the `Deactivate` method. The interface must derive from `IGrainExtension`.

```csharp
public interface IGrainDeactivateExtension : IGrainExtension
{
    Task Deactivate(string msg);
}
```

### Deactivate extension implementation

Next, implement the `GrainDeactivateExtension` class, providing the implementation for the `Deactivate` method.

To access the target grain, retrieve the <xref:Orleans.Runtime.IGrainContext> from the constructor. It's injected via dependency injection when creating the extension.

```csharp
public sealed class GrainDeactivateExtension : IGrainDeactivateExtension
{
    private IGrainContext _context;

    public GrainDeactivateExtension(IGrainContext context)
    {
        _context = context;
    }

    public Task Deactivate(string msg)
    {
        var reason = new DeactivationReason(DeactivationReasonCode.ApplicationRequested, msg);
        _context.Deactivate(reason);
        return Task.CompletedTask;
    }
}
```

### Deactivate extension registration and usage

Now that the interface and implementation are defined, register the extension when configuring the silo using the <xref:Orleans.Hosting.HostingGrainExtensions.AddGrainExtension*> method.

```csharp
siloBuilder.AddGrainExtension<IGrainDeactivateExtension, GrainDeactivateExtension>();
```

To use the extension on any grain, retrieve a reference to the extension and call its `Deactivate` method.

```csharp
var grain = client.GetGrain<SomeExampleGrain>(someKey);
var grainReferenceAsInterface = grain.AsReference<IGrainDeactivateExtension>();

await grainReferenceAsInterface.Deactivate("Because, I said so...");
```

## State manipulation extension example

This example demonstrates adding `GetState` and `SetState` methods to any grain through extensions, allowing manipulation of the grain's internal state.

### State manipulation extension interface

First, define the `IGrainStateAccessor<T>` interface containing the `GetState` and `SetState` methods. Again, this interface must derive from `IGrainExtension`.

```csharp
public interface IGrainStateAccessor<T> : IGrainExtension
{
    Task<T> GetState();
    Task SetState(T state);
}
```

Once access to the target grain is available, use the extension to manipulate its state. In this example, use an extension to access and modify a specific integer state value within the target grain.

### State manipulation extension implementation

The extension used is `IGrainStateAccessor<T>`, providing methods to get and set a state value of type `T`. To create the extension, implement the interface in a class taking a `getter` and a `setter` as arguments in its constructor.

```csharp
public sealed class GrainStateAccessor<T> : IGrainStateAccessor<T>
{
    private readonly Func<T> _getter;
    private readonly Action<T> _setter;

    public GrainStateAccessor(Func<T> getter, Action<T> setter)
    {
        _getter = getter;
        _setter = setter;
    }

    public Task<T> GetState()
    {
        return Task.FromResult(_getter.Invoke());
    }

    public Task SetState(T state)
    {
        _setter.Invoke(state);
        return Task.CompletedTask;
    }
}
```

In the preceding implementation, the `GrainStateAccessor<T>` class takes `getter` and `setter` arguments in its constructor. These delegates read and modify the target grain's state. The `GetState()` method returns a <xref:System.Threading.Tasks.Task`1> wrapping the current value of the `T` state, while the `SetState(T state)` method sets the new value of the `T` state.

### State manipulation extension registration and usage

To use the extension to access and modify the target grain's state, register the extension and set its components in the target grain's <xref:Orleans.Grain.OnActivateAsync?displayProperty=nameWithType> method.

```csharp
public override Task OnActivateAsync()
{
    // Retrieve the IGrainStateAccessor<T> extension
    var accessor = new GrainStateAccessor<int>(
        getter: () => this.Value,
        setter: value => this.Value = value);

    // Set the extension as a component of the target grain's context
    ((IGrainBase)this).GrainContext.SetComponent<IGrainStateAccessor<int>>(accessor);

    return base.OnActivateAsync();
}
```

In the preceding example, create a new instance of `GrainStateAccessor<int>` taking a `getter` and `setter` for an integer state value. The `getter` reads the `Value` property of the target grain, while the `setter` sets the new value of the `Value` property. Then, set this instance as a component of the target grain's context using the <xref:Orleans.Runtime.IGrainContext.SetComponent*?displayProperty=nameWithType> method.

Once the extension is registered, use it to get and set the target grain's state by accessing it through a reference to the extension.

```csharp
// Get a reference to the IGrainStateAccessor<int> extension
var accessor = grain.AsReference<IGrainStateAccessor<int>>();

// Get the current value of the state
var value = await accessor.GetState();

// Set a new value of the state
await accessor.SetState(10);
```

In the preceding example, get a reference to the `IGrainStateAccessor<int>` extension for a specific grain instance using the <xref:Orleans.GrainExtensions.AsReference*> method. Then, use this reference to call the `GetState()` and `SetState(T state)` methods to read and modify the state value of the target grain.

## See also

- [Develop a grain](index.md)
- [Grain identity](grain-identity.md)
- [Grain lifecycle overview](grain-lifecycle.md)
- [Grain placement](grain-placement.md)
