---
layout: page
title: Using Immutable<T> to Optimize Copying
---
{% include JB/setup %}

# Using Immutable<T> to Optimize Copying
Orleans has a feature that can be used to avoid some of the overhead associated with serializing request messages. This note starts with a general description of how serialization works, and then explains how to use the new feature.

## Serialization in Orleans
When a grain method is invoked, the Orleans runtime makes a deep copy of the method arguments and forms the request out of the copies. This protects against the calling code modifying the argument objects before the data is passed to the called grain.

If the called grain is on a different silo, then the copies are eventually serialized into a byte stream and sent over the network to the target silo, where they are deserialized back into objects. If the called grain is on the same silo, then the copies are handed directly to the called method.

Return values are handled the same way: first copied, then possibly serialized and deserialized.

Note that all 3 processes, copying, serializing, and deserializing, respect object identity. In other words, if you pass a list that has the same object in it twice, on the receiving side you'll get a list with the same object in it twice, rather than with two objects with the same values in them.

Also note that Orleans doesn't use the .NET serializer or the data contract serializer. Orleans uses a mix of hand-crafted code for common system types (collections, primitives, and a few others) and generated code for application types. If a type with neither a hand-crafted or generated serializer is encountered, Orleans will fall back to the .NET serializer, but this should be a rare occurrence. We try to avoid this because the .NET serializer is significantly slower than the Orleans serializer, and creates significantly larger messages.

## Optimizing Copying
In many cases, the deep copying is unnecessary. For instance, a possible scenario is a web front-end that receives a byte array from its client and passes that request, including the byte array, on to a grain for processing. The front-end process doesn't do anything with the array once it has passed it on to the grain; in particular, it doesn't reuse the array to receive a future request. Inside the grain, the byte array is parsed to fetch the input data, but not modified. The grain returns another byte array that it has created to get passed back to the web client; it discards the array as soon as it returns it. The web front-end passes the result byte array back to its client, without modification.

 In such a scenario, there is no need to copy either the request or response byte arrays. Unfortunately, the Orleans runtime can't figure this out by itself, since it can't tell whether or not the arrays are modified later on by the web front-end or by the grain. In the best of all possible worlds, we'd have some sort of .NET mechanism for indicating that a value is no longer modified; lacking that, we've added an Orleans-specific mechanism for this: the Immutable<T> wrapper class.

**Immutable<T>**

The Orleans.Concurrency.Immutable<T> wrapper class is used to indicate that a value may be considered immutable; that is, the underlying value will not be modified, so no copying is required for safe sharing. Note that using Immutable<T> implies that neither the provider of the value nor the recipient of the value will modify it in the future; it is not a one-sided commitment, but rather a mutual dual-side commitment.

**Using Immutable<T>**

Using Immutable<T> is simple: in your grain interface, instead of passing T, pass Immutable<T>. For instance, in the above described scenario, the grain method that was:

``` csharp
Task<byte[]> ProcessRequest(byte[] request);
```

 becomes:

``` csharp
Task<Immutable<byte[]>> ProcessRequest(Immutable<byte[]> request);
```

To create an Immutable<T>, simply use the constructor:

``` csharp
Immutable<byte[]> immutable = new Immutable<byte[]>(buffer);
```

 And to get the value inside the immutable, use the .Value property:

``` csharp
byte[] buffer = immutable.Value;
```

 You can also get a deep copy of the value inside the Immutable, in case you need a value that can be modified, using the GetCopy method:

``` csharp
byte[] buffer = immutable.GetCopy();
```

**What Is Immutable?**

For the purposes of the Immutable<> class, immutability is a rather strict statement: the contents of the data item will not be modified in any way that could change the item's semantic meaning, or that would interfere with another thread simultaneously accessing the item. The safest way to ensure this is to simply not modify the item at all: bitwise immutability, rather than logical immutability. 

In some cases it is safe to relax this to logical immutability, but care must be taken to ensure that the mutating code is properly thread-safe; because dealing with multithreading is complex, and uncommon in an Orleans context, we strongly recommend against this approach and recommend sticking to bitwise immutability.