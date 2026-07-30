---
title: Serialization of immutable types in Orleans
description: Learn how .NET Orleans handles type immutability in the context of serialization.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Serialization of immutable types in Orleans

Orleans has a feature you can use to avoid some overhead associated with serializing messages containing immutable types. This section describes the feature and its application, starting with the context where it's relevant.

## Serialization in Orleans

When you invoke a grain method, the Orleans runtime makes a deep copy of the method arguments and forms the request from these copies. This protects against the calling code modifying the argument objects before the data passes to the called grain.

If the called grain is on a different silo, the copies are eventually serialized into a byte stream and sent over the network to the target silo, where they are deserialized back into objects. If the called grain is on the same silo, the copies are handed directly to the called method.

Return values are handled the same way: first copied, then possibly serialized and deserialized.

Note that all three processes—copying, serializing, and deserializing—respect object identity. In other words, if you pass a list containing the same object twice, the receiving side gets a list with the same object twice, rather than two objects with the same values.

## Optimize copying

In many cases, deep copying is unnecessary. For instance, consider a scenario where a web front-end receives a byte array from its client and passes that request, including the byte array, to a grain for processing. The front-end process does nothing with the array after passing it to the grain; specifically, it doesn't reuse the array for future requests. Inside the grain, the byte array is parsed to fetch input data but isn't modified. The grain returns another byte array it created back to the web client and discards the array immediately after returning it. The web front-end passes the result byte array back to its client without modification.

In such a scenario, there's no need to copy either the request or response byte arrays. Unfortunately, the Orleans runtime can't figure this out automatically, as it can't determine whether the web front-end or the grain modifies the arrays later. Ideally, a .NET mechanism would indicate that a value is no longer modified. Lacking that, we've added Orleans-specific mechanisms: the <xref:Orleans.Concurrency.Immutable`1> wrapper class and the <xref:Orleans.Concurrency.ImmutableAttribute>.

### Use the <xref:Orleans.Concurrency.ImmutableAttribute> attribute to mark a type, parameter, property, or field as immutable

For user-defined types, you can add the <xref:Orleans.Concurrency.ImmutableAttribute> to the type. This instructs the Orleans serializer to avoid copying instances of this type. The following code snippet demonstrates using <xref:Orleans.Concurrency.ImmutableAttribute> to denote an immutable type. This type won't be copied during transmission.

```csharp
[Immutable]
public class MyImmutableType
{
    public int MyValue { get; }

    public MyImmutableType(int value)
    {
        MyValue = value;
    }
}
```

Sometimes, you might not control the object; for example, it might be a `List<int>` you're sending between grains. Other times, parts of your objects might be immutable while others aren't. For these cases, Orleans supports additional options.

1. Method signatures can include <xref:Orleans.ImmutableAttribute> on a per-parameter basis:

    ```csharp
    public interface ISummerGrain : IGrain
    {
      // `values` will not be copied.
      ValueTask<int> Sum([Immutable] List<int> values);
    }
    ```

1. Mark individual properties and fields as <xref:Orleans.ImmutableAttribute> to prevent copies when instances of the containing type are copied.

    ```csharp
    [GenerateSerializer]
    public sealed class MyType
    {
        [Id(0), Immutable]
        public List<int> ReferenceData { get; set; }

        [Id(1)]
        public List<int> RunningTotals { get; set; }
    }
    ```

### Use <xref:Orleans.Concurrency.Immutable`1>

Use the <xref:Orleans.Concurrency.Immutable`1> wrapper class to indicate a value can be considered immutable; that is, the underlying value won't be modified, so no copying is required for safe sharing. Note that using <xref:Orleans.Concurrency.Immutable`1> implies neither the provider nor the recipient of the value will modify it in the future. It's a mutual, dual-sided commitment, not a one-sided one.

To use <xref:Orleans.Concurrency.Immutable`1> in your grain interface, pass <xref:Orleans.Concurrency.Immutable`1> instead of `T`. For instance, in the scenario described above, the grain method was:

```csharp
Task<byte[]> ProcessRequest(byte[] request);
```

Which would then become:

```csharp
Task<Immutable<byte[]>> ProcessRequest(Immutable<byte[]> request);
```

To create an <xref:Orleans.Concurrency.Immutable`1>, simply use its constructor:

```csharp
Immutable<byte[]> immutable = new(buffer);
```

To get the value inside the immutable wrapper, use the `.Value` property:

```csharp
byte[] buffer = immutable.Value;
```

## Immutability in Orleans

For Orleans' purposes, immutability is a strict statement: the contents of the data item won't be modified in any way that could change the item's semantic meaning or interfere with another thread simultaneously accessing it. The safest way to ensure this is simply not to modify the item at all: use bitwise immutability rather than logical immutability.

In some cases, it's safe to relax this to logical immutability, but you must take care to ensure the mutating code is properly thread-safe. Because dealing with multithreading is complex and uncommon in an Orleans context, we strongly recommend against this approach and advise sticking to bitwise immutability.
