---
title: Grain references
description: Create, use, and reason about grain references in Orleans 10.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain references

A grain reference is a generated proxy that implements a grain interface and addresses one logical grain. It contains the grain's [identity](grain-identity.md) and the interface used to call it, but it doesn't contain the activation's network location.

References remain valid when Orleans deactivates, recreates, or migrates the target activation. Getting a reference is a local operation and doesn't activate the grain.

## Get a reference

Use <xref:Orleans.IGrainFactory.GetGrain*> from a client, hosted service, or grain:

```csharp
public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> Add(int amount);
}

ICounterGrain counter =
    grainFactory.GetGrain<ICounterGrain>("orders-processed");

int value = await counter.Add(1);
```

Within a class deriving from <xref:Orleans.Grain>, use its `GrainFactory` property. In other services, inject <xref:Orleans.IGrainFactory> or <xref:Orleans.IClusterClient>.

Grain references are serializable. They can be passed as grain method arguments, returned from calls, and included in persistent state.

## Interface-to-type resolution

The interface and key are usually enough for Orleans to identify the grain type. If exactly one grain class implements the interface, Orleans maps the interface to that class.

When multiple classes implement the same interface, prefer distinct marker interfaces:

```csharp
public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> Add(int amount);
}

public interface IUpCounterGrain : ICounterGrain;

public interface IDownCounterGrain : ICounterGrain;

public sealed class UpCounterGrain : Grain, IUpCounterGrain
{
    private int _value;

    public ValueTask<int> Add(int amount) =>
        ValueTask.FromResult(_value += amount);
}

public sealed class DownCounterGrain : Grain, IDownCounterGrain
{
    private int _value;

    public ValueTask<int> Add(int amount) =>
        ValueTask.FromResult(_value -= amount);
}
```

```csharp
IUpCounterGrain up =
    grainFactory.GetGrain<IUpCounterGrain>("counter");

IDownCounterGrain down =
    grainFactory.GetGrain<IDownCounterGrain>("counter");
```

The two references have the same key but different grain types, so they address different logical grains.

For compatibility scenarios, <xref:Orleans.Metadata.DefaultGrainTypeAttribute> can select the default implementation for an interface, and `GetGrain` overloads accepting a grain class name prefix can disambiguate implementations. Explicit marker interfaces are usually clearer and safer to refactor.

## Cast a reference to another supported interface

If the same grain implementation supports another grain interface or extension interface, use <xref:Orleans.GrainExtensions.AsReference*>:

```csharp
IUserGrain user = grainFactory.GetGrain<IUserGrain>("user-42");
IUserProfileGrain profile = user.AsReference<IUserProfileGrain>();
```

This doesn't create a different grain. It creates another typed proxy for the same grain identity. The target grain type must support the requested interface.

Within a grain, pass a reference to itself instead of passing `this`:

```csharp
IUserGrain self = this.AsReference<IUserGrain>();
```

## Reference equality and storage

Multiple proxy objects can refer to the same grain. Compare logical identity when identity matters rather than relying on CLR object identity. Persist references only when retaining the interface relationship is useful; persist domain keys when the application should resolve the current interface at use time.

## Advanced: resolve from GrainId

Some `GetGrain` overloads accept <xref:Orleans.Runtime.GrainId>. These are intended for framework and infrastructure code that already has a resolved grain type and encoded key. Most application code should use a typed key overload because it preserves compile-time key and interface checks.
