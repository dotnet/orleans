---
title: Grain references
description: Learn about grain references in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Grain references

Before calling a method on a grain, you first need a reference to that grain. A grain reference is a proxy object implementing the same grain interface as the corresponding grain class. It encapsulates the logical identity (type and unique key) of the target grain. You use grain references to make calls to the target grain. Each grain reference points to a single grain (a single instance of the grain class), but you can create multiple independent references to the same grain.

Since a grain reference represents the logical identity of the target grain, it's independent of the grain's physical location and remains valid even after a complete system restart. You can use grain references like any other .NET object. You can pass it to a method, use it as a method return value, and even save it to persistent storage.

You can obtain a grain reference by passing the identity of a grain to the <xref:Orleans.IGrainFactory.GetGrain``1(System.Type,System.Guid)?displayProperty=nameWithType> method, where `T` is the grain interface and `key` is the unique key of the grain within its type.

The following examples show how to obtain a grain reference for the `IPlayerGrain` interface defined previously.

From within a grain class:

```csharp
// This would typically be read from an HTTP request parameter or elsewhere.
Guid playerId = Guid.NewGuid();
IPlayerGrain player = GrainFactory.GetGrain<IPlayerGrain>(playerId);
```

From Orleans client code:

```csharp
// This would typically be read from an HTTP request parameter or elsewhere.
Guid playerId = Guid.NewGuid();
IPlayerGrain player = client.GetGrain<IPlayerGrain>(playerId);
```

Grain references contain three pieces of information:

1. The grain _type_, which uniquely identifies the grain class.
1. The grain _key_, which uniquely identifies a logical instance of that grain class.
1. The _interface_ which the grain reference must implement.

> [!NOTE]
> The grain _type_ and _key_ form the [grain identity](grain-identity.md).

Notice that the preceding calls to <xref:Orleans.IGrainFactory.GetGrain*?displayProperty=nameWithType> accepted only two of these three things:

- The _interface_ implemented by the grain reference, `IPlayerGrain`.
- The grain _key_, which is the value of `playerId`.

Despite stating that a grain reference contains a grain _type_, _key_, and _interface_, the examples only provided Orleans with the _key_ and _interface_. This is because Orleans maintains a mapping between grain interfaces and grain types. When you ask the grain factory for `IShoppingCartGrain`, Orleans consults its mapping to find the corresponding grain type so it can create the reference. This works when there's only one implementation of a grain interface. However, if there are multiple implementations, you need to disambiguate them in the <xref:Orleans.IGrainFactory.GetGrain*> call. For more information, see the next section, [Disambiguating grain type resolution](#disambiguating-grain-type-resolution).

> [!NOTE]
> Orleans generates grain reference implementation types for each grain interface in your application during compilation. These grain reference implementations inherit from the <xref:Orleans.Runtime.GrainReference?displayProperty=nameWithType> class. <xref:Orleans.IGrainFactory.GetGrain*> returns instances of the generated <xref:Orleans.Runtime.GrainReference?displayProperty=nameWithType> implementation corresponding to the requested grain interface.

## Disambiguating grain type resolution

When multiple implementations of a grain interface exist, such as in the following example, Orleans attempts to determine the intended implementation when creating a grain reference. Consider the following example, where there are two implementations of the `ICounterGrain` interface:

```csharp
public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> UpdateCount();
}

public class UpCounterGrain : ICounterGrain
{
    private int _count;

    public ValueTask<string> UpdateCount() => new(++_count); // Increment count
}

public class DownCounterGrain : ICounterGrain
{
    private int _count;

    public ValueTask<string> UpdateCount() => new(--_count); // Decrement count
}
```

The following call to <xref:Orleans.IGrainFactory.GetGrain*> throws an exception because Orleans doesn't know how to unambiguously map `ICounterGrain` to one of the grain classes.

```csharp
// This will throw an exception: there is no unambiguous mapping from ICounterGrain to a grain class.
ICounterGrain myCounter = grainFactory.GetGrain<ICounterGrain>("my-counter");
```

An <xref:System.ArgumentException?displayProperty=nameWithType> is thrown with the following message:

```Output
Unable to identify a single appropriate grain type for interface ICounterGrain. Candidates: upcounter (UpCounterGrain), downcounter (DownCounterGrain)
```

The error message tells you which grain implementations Orleans found that match the requested grain interface type, `ICounterGrain`. It shows the grain type names (`upcounter` and `downcounter`) and the grain classes (`UpCounterGrain` and `DownCounterGrain`).

> [!NOTE]
> The grain type names in the preceding error message, `upcounter` and `downcounter`, are derived from the grain class names, `UpCounterGrain` and `DownCounterGrain` respectively. This is the default behavior in Orleans and it can be customized by adding a `[GrainType(string)]` attribute to the grain class. For example:
>
> ```csharp
> [GrainType("up")]
> public class UpCounterGrain : IUpCounterGrain { /* as above */ }
> ```

There are several ways to resolve this ambiguity, detailed in the following subsections.

### Disambiguating grain types using unique marker interfaces

The clearest way to disambiguate these grains is to give them unique grain interfaces. For example, if you add the interface `IUpCounterGrain` to the `UpCounterGrain` class and add the interface `IDownCounterGrain` to the `DownCounterGrain` class, as in the following example, you can resolve the correct grain reference by passing `IUpCounterGrain` or `IDownCounterGrain` to the <xref:Orleans.IGrainFactory.GetGrain*> call instead of the ambiguous `ICounterGrain` type.

```csharp
public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> UpdateCount();
}

// Define unique interfaces for our implementations
public interface IUpCounterGrain : ICounterGrain, IGrainWithStringKey {}
public interface IDownCounterGrain : ICounterGrain, IGrainWithStringKey {}

public class UpCounterGrain : IUpCounterGrain
{
    private int _count;

    public ValueTask<string> UpdateCount() => new(++_count); // Increment count
}

public class DownCounterGrain : IDownCounterGrain
{
    private int _count;

    public ValueTask<string> UpdateCount() => new(--_count); // Decrement count
}
```

To create a reference to either grain, consider the following code:

```csharp
// Get a reference to an UpCounterGrain.
ICounterGrain myUpCounter = grainFactory.GetGrain<IUpCounterGrain>("my-counter");

// Get a reference to a DownCounterGrain.
ICounterGrain myDownCounter = grainFactory.GetGrain<IDownCounterGrain>("my-counter");
```

> [!NOTE]
> In the preceding example, you created two grain references with the same key but different grain types. The first, stored in the `myUpCounter` variable, references the grain with the ID `upcounter/my-counter`. The second, stored in the `myDownCounter` variable, references the grain with the ID `downcounter/my-counter`. The combination of grain _type_ and grain _key_ uniquely identifies a grain. Therefore, `myUpCounter` and `myDownCounter` refer to different grains.

#### Disambiguating grain types by providing a grain class prefix

You can provide a grain class name prefix to <xref:Orleans.IGrainFactory.GetGrain*?displayProperty=nameWithType>, for example:

```csharp
ICounterGrain myUpCounter = grainFactory.GetGrain<ICounterGrain>("my-counter", grainClassNamePrefix: "Up");
ICounterGrain myDownCounter = grainFactory.GetGrain<ICounterGrain>("my-counter", grainClassNamePrefix: "Down");
```

### Specifying the default grain implementation using the naming convention

When disambiguating multiple implementations of the same grain interface, Orleans selects an implementation using the convention of stripping a leading 'I' from the interface name. For example, if the interface name is `ICounterGrain` and there are two implementations, `CounterGrain` and `DownCounterGrain`, Orleans chooses `CounterGrain` when asked for a reference to `ICounterGrain`, as in the following example:

```csharp
/// This will refer to an instance of CounterGrain, since that matches the convention.
ICounterGrain myUpCounter = grainFactory.GetGrain<ICounterGrain>("my-counter");
```

### Specifying the default grain type using an attribute

You can add the <xref:Orleans.Metadata.DefaultGrainTypeAttribute?displayProperty=nameWithType> attribute to a grain interface to specify the grain type of the default implementation for that interface, as shown in the following example:

```csharp
[DefaultGrainType("up-counter")]
public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> UpdateCount();
}

[GrainType("up-counter")]
public class UpCounterGrain : ICounterGrain
{
    private int _count;

    public ValueTask<string> UpdateCount() => new(++_count); // Increment count
}

[GrainType("down-counter")]
public class DownCounterGrain : ICounterGrain
{
    private int _count;

    public ValueTask<string> UpdateCount() => new(--_count); // Decrement count
}
```

```csharp
/// This will refer to an instance of UpCounterGrain, due to the [DefaultGrainType("up-counter"')] attribute
ICounterGrain myUpCounter = grainFactory.GetGrain<ICounterGrain>("my-counter");
```

### Disambiguating grain types by providing the resolved grain ID

Some overloads of <xref:Orleans.IGrainFactory.GetGrain*?displayProperty=nameWithType> accept an argument of type <xref:Orleans.Runtime.GrainId?displayProperty=nameWithType>. When using these overloads, Orleans doesn't need to map from an interface type to a grain type, so there's no ambiguity to resolve. For example:

```csharp
public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> UpdateCount();
}

[GrainType("up-counter")]
public class UpCounterGrain : ICounterGrain
{
    private int _count;

    public ValueTask<string> UpdateCount() => new(++_count); // Increment count
}

[GrainType("down-counter")]
public class DownCounterGrain : ICounterGrain
{
    private int _count;

    public ValueTask<string> UpdateCount() => new(--_count); // Decrement count
}
```

```csharp
// This will refer to an instance of UpCounterGrain, since "up-counter" was specified as the grain type
// and the UpCounterGrain uses [GrainType("up-counter")] to specify its grain type.
ICounterGrain myUpCounter = grainFactory.GetGrain<ICounterGrain>(GrainId.Create("up-counter", "my-counter"));
```
