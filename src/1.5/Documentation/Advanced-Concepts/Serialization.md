---
layout: page
title: Serialization and Writing Custom Serializers
---

# Serialization and Writing Custom Serializers

Orleans has an advanced and extensible serialization framework. Orleans serializes data types passed in grain request and response messages as well as grain persistent state objects. As part of this framework, Orleans automatically generates serialization code for those data types. In addition to generating a more efficient serialization/deserialization for types that are already .NET-serializable, Orleans also tries to generate serializers for types used in grain interfaces that are not .NET-serializable. The framework also includes a set of efficient built-in serializers for frequently used types: lists, dictionaries, strings, primitives, arrays, etc.

There are 2 important features of Orleans's serializer that set it apart from a lot of other third party serialization frameworks: dynamic types/arbitrary polymorphism and object identity.

1. **Dynamic types and arbitrary polymorphism** - Orleans does not put any restrictions on the types that can be passed in grain calls and maintains the dynamic nature of the actual data type. That means, for example, that if the method in the grain interfaces is declared to accept `IDictionary` but at runtime the sender passes `SortedDictionary`, the receiver will indeed get `SortedDictionary` (although the "static contract"/grain interface did not specify this behaviour).

2. **Maintaining Object identity** - if the same object is passed multiple types in the arguments of a grain call or is indirectly pointed more than once from the arguments, Orleans will serialize it only once. At the receiver side Orleans will restore all references correctly, so that two pointers to the same object still point to the same object after deserialization as well. Object identity is important to preserve in scenarios like the following. Imagine actor A is sending a dictionary with 100 entries to actor B, and 10 of the keys in the dictionary point to the same object, obj, on A's side. Without preserving object identity, B would receive a dictionary of 100 entries with those 10 keys pointing to 10 different clones of obj. With object identity preserved, the dictionary on B's side looks exactly like on A's side with those 10 keys pointing to a single object obj.

The above two behaviours are provided by the standard .NET binary serializer and it was therefore important for us to support this standard and familiar behaviour in Orleans as well.

# Generated Serializers

Orleans uses the following rules to decide which serializers to generate.
The rules are:

1) Scan all types in all assemblies which reference the core Orleans library.

2) Out of those assemblies: generate serializers for types that are directly referenced in grain interfaces method signatures or state class signature or for any type that is marked with  `[Serializable]` attribute.

3) In addition, a grain interface or implementation project can point to arbitrary types for serialization generation by adding a `[KnownType]` or `[KnownAssembly]` assembly level attributes to tell code generator to generate serializers for a specific types or all eligible types within an assembly.


# Serialization Providers
Orleans supports integration with third-party serializers using a provider model. This requires an implementation of the `IExternalSerializer` type described in the custom serialization section of this document. Integrations for some common serializers are maintained alongside Orleans, for example:
* [Protocol Buffers](https://developers.google.com/protocol-buffers/): `Orleans.Serialization.ProtobufSerializer` from the [Microsoft.Orleans.OrleansGoogleUtils](https://www.nuget.org/packages/Microsoft.Orleans.OrleansGoogleUtils/) NuGet package.
* [Bond](https://github.com/microsoft/bond/): `Orleans.Serialization.BondSerializer` from the [Microsoft.Orleans.Serialization.Bond](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.Bond/) NuGet package.
* [Newtonsoft.Json AKA Json.NET](http://www.newtonsoft.com/json): `Orleans.Serialization.OrleansJsonSerializer` from the core Orleans library.

Custom implementation of `IExternalSerializer` is described in the Writing Custom Serializers section below.

### Configuration
It is important to ensure that serialization configuration is identical on all clients and silos. If configurations are not consistent, serialization errors may occur.

Serialization providers, which implement `IExternalSerializer`, can be specified using the `SerializationProviders` property of `ClientConfiguration` and `GlobalConfiguration` in code:

```C#
var cfg = new ClientConfiguration();
cfg.SerializationProviders.Add(typeof(FantasticSerializer).GetTypeInfo());
```
```C#
var cfg = new GlobalConfiguration();
cfg.SerializationProviders.Add(typeof(FantasticSerializer).GetTypeInfo());
```

Alternatively, they can be specified in XML configuration under the `<SerializationProviders />` property of `<Messaging>`:
``` xml
<Messaging>
  <SerializationProviders>
    <Provider type="GreatCompany.FantasticSerializer, GreatCompany.SerializerAssembly"/>
  </SerializationProviders>
</Messaging>
```

In both cases, multiple providers can be configured. The collection is ordered, meaning that if a provider which can serialize types `A` and `B` is specified before a provider which can only serialize type `B`, then the latter provider will not be used.

# Writing Custom Serializers

In addition to automatic serialization generation, application code can provide custom serialization for types it chooses. Orleans recommends using the automatic serialization generation for the majority of your application types and only write custom serializers in rare cases when you believe it is possible to get improved performance by hand-coding serializers. This note describes how to do so, and identifies some specific cases when it might be helpful.

There are 3 ways in which applications can customize serialization:

1. Add serialization methods to your type and mark them with appropriate attributes (`CopierMethod`, `SerializerMethod`, `DeserializerMethod`). This method is preferable for types that your application owns, that is, the types that you can add new methods to.

2. Implement `IExternalSerializer` and register it during configuration time. This method is useful for integrating an external serialization library.

3. Write a separate static class annotated with an `[Serializer(typeof(YourType))]` with the 3 serialization methods in it and the same attributes as above. This method is useful for types that the application does not own, for example, types defined in other libraries your application has no control over.

Each of these methods are detailed in the sections below.

## Introduction
Orleans serialization happens in three stages: objects are immediately deep copied to ensure isolation; before being put on the wire; objects are serialized to a message byte stream; and when delivered to the target activation, objects are recreated (deserialized) from the received byte stream. Data types that may be sent in messages -- that is, types that may be passed as method arguments or return values -- must have associated routines that perform these three steps. We refer to these routines collectively as the serializers for a data type.

 The copier for a type stands alone, while the serializer and deserializer are a pair that work together. You can provide just a custom copier, or just a custom serializer and a custom deserializer, or you can provide custom implementations of all three.

 Serializers are registered for each supported data type at silo start-up and whenever an assembly is loaded. Registration is necessary for custom serializer routines for a type to be used. Serializer selection is based on the dynamic type of the object to be copied or serialized. For this reason, there is no need to create serializers for abstract classes or interfaces, because they will never be used.

## When to Consider Writing a Custom Serializer
It is rare that a hand-crafted serializer routine will perform meaningfully better than the generated versions. If you are tempted to do so, you should first consider the following options:

 If there are fields or properties within your data types that don't have to be serialized or copied, you can mark them with the `NonSerialized` attribute. This will cause the generated code to skip these fields when copying and serializing.
 Use `Immutable<T>` & `[Immutable]` where possible to avoid copying immutable data. The section on *Optimizing Copying* below for details. If you're avoiding using the standard generic collection types, don't. The Orleans runtime contains custom serializers for the generic collections that use the semantics of the collections to optimize copying, serializing, and deserializing. These collections also have special "abbreviated" representations in the serialized byte stream, resulting in even more performance advantages. For instance, a `Dictionary<string, string>` will be faster than a `List<Tuple<string, string>>`.

 The most common case where a custom serializer can provide a noticeable performance gain is when there is significant semantic information encoded in the data type that is not available by simply copying field values. For instance, arrays that are sparsely populated may often be more efficiently serialized by treating the array as a collection of index/value pairs, even if the application keeps the data as a fully realized array for speed of operation.

 A key thing to do before writing a custom serializer is to make sure that the generated serializer is really hurting your performance. Profiling will help a bit here, but even more valuable is running end-to-end stress tests of your application with varying serialization loads to gauge the system-level impact, rather than the micro-impact of serialization. For instance, building a test version that passes no parameters to or results from grain methods, simply using canned values at either end, will zoom in on the impact of serialization and copying on system performance.

## Method 1: Adding Serialization Methods to the Type

All serializer routines should be implemented as static members of the class or struct they operate on. The names shown here are not required; registration is based on the presence of the respective attributes, not on method names. Note that serializer methods need not be public.

Unless you implement all three serialization routines, you should mark your type with the `Serializable` attribute so that the missing methods will be generated for you.

### Copier
Copier methods are flagged with the `Orleans.CopierMethod` attribute:

``` csharp
[CopierMethod]
static private object Copy(object input, ICopyContext context)
{
    ...
}
```

Copiers are usually the simplest serializer routines to write. They take an object, guaranteed to be of the same type as the type the copier is defined in, and must return a semantically-equivalent copy of the object.

If, as part of copying the object, a sub-object needs to be copied, the best way to do so is to use the SerializationManager's DeepCopyInner routine:

``` csharp
var fooCopy = SerializationManager.DeepCopyInner(foo, context);
```

It is important to use DeepCopyInner, instead of DeepCopy, in order to maintain the object identity context for the full copy operation.

**Maintaining Object Identity**

An important responsibility of a copy routine is to maintain object identity. The Orleans runtime provides a helper class for this. Before copying a sub-object "by hand" (i.e., not by calling DeepCopyInner), check to see if it has already been referenced as follows:


``` csharp
var fooCopy = context.CheckObjectWhileCopying(foo);

if (fooCopy == null)
{
    // Actually make a copy of foo
    context.RecordObject(foo, fooCopy);
}
```


The last line, the call to `RecordObject`, is required so that possible future references to the same object as foo references will get found properly by `CheckObjectWhileCopying`.

Note that this should only be done for class instances, not struct instances or .NET primitives (strings, Uris, enums).

If you use `DeepCopyInner` to copy sub-objects, then object identity is handled for you.

### Serializer
Serialization methods are flagged with the `SerializerMethod` attribute:


``` csharp
[SerializerMethod]
static private void Serialize(object input, ISerializationContext context, Type expected)
{
    ...
}
```

As with copiers, the "input" object passed to a serializer is guaranteed to be an instance of the defining type. The "expected" type may be ignored; it is based on compile-time type information about the data item, and is used at a higher level to form the type prefix in the byte stream.

To serialize sub-objects, use the `SerializationManager`'s `SerializeInner` routine:

``` csharp
SerializationManager.SerializeInner(foo, context, typeof(FooType));
```

If there is no particular expected type for foo, then you can pass null for the expected type.

The `BinaryTokenStreamWriter` class provides a wide variety of methods for writing data to the byte stream. An instance of the class can be obtained via the `context.StreamWriter` property. See the class for documentation.

### Deserializer
Deserialization methods are flagged with the `DeserializerMethod` attribute:

``` csharp
[DeserializerMethod]
static private object Deserialize(Type expected, IDeserializationContext context)
{
    ...
}
```

The "expected" type may be ignored; it is based on compile-time type information about the data item, and is used at a higher level to form the type prefix in the byte stream. The actual type of the object to be created will always be the type of the class in which the deserializer is defined.

To deserialize sub-objects, use the `SerializationManager`'s `DeserializeInner` routine:

``` csharp
var foo = SerializationManager.DeserializeInner(typeof(FooType), context);
```

Or, alternatively,

``` csharp
var foo = SerializationManager.DeserializeInner<FooType>(context);
```

If there is no particular expected type for foo, use the non-generic `DeserializeInner` variant and pass `null` for the expected type.

The `BinaryTokenStreamReader` class provides a wide variety of methods for reading data from the byte stream. An instance of the class can be obtained via the `context.StreamReader` property. See the class for documentation.

## Method 2: Writing a Serializer Provider
In this method, you implement `Orleans.Serialization.IExternalSerializer` and add it to the `SerializationProviders` property on both `ClientConfiguration` on the client and `GlobalConfiguration` on the silos. Configuration is detailed in the Serialization Providers section above.

Implementation of `IExternalSerializer` follows the pattern described for serialization methods from `Method 1` above with the addition of an `Initialize` method and an `IsSupportedType` method which Orleans uses to determine if the serializer supports a given type. This is the interface definition:
``` csharp
public interface IExternalSerializer
{
    /// <summary>
    /// Initializes the external serializer. Called once when the serialization manager creates 
    /// an instance of this type
    /// </summary>
    void Initialize(Logger logger);

    /// <summary>
    /// Informs the serialization manager whether this serializer supports the type for serialization.
    /// </summary>
    /// <param name="itemType">The type of the item to be serialized</param>
    /// <returns>A value indicating whether the item can be serialized.</returns>
    bool IsSupportedType(Type itemType);

    /// <summary>
    /// Tries to create a copy of source.
    /// </summary>
    /// <param name="source">The item to create a copy of</param>
    /// <param name="context">The context in which the object is being copied.</param>
    /// <returns>The copy</returns>
    object DeepCopy(object source, ICopyContext context);

    /// <summary>
    /// Tries to serialize an item.
    /// </summary>
    /// <param name="item">The instance of the object being serialized</param>
    /// <param name="context">The context in which the object is being serialized.</param>
    /// <param name="expectedType">The type that the deserializer will expect</param>
    void Serialize(object item, ISerializationContext context, Type expectedType);

    /// <summary>
    /// Tries to deserialize an item.
    /// </summary>
    /// <param name="context">The context in which the object is being deserialized.</param>
    /// <param name="expectedType">The type that should be deserialized</param>
    /// <returns>The deserialized object</returns>
    object Deserialize(Type expectedType, IDeserializationContext context);
}
```

## Method 3: Writing a Serializer for Individual Types

In this method you write a new class annotated with an attribute `[SerializerAttribute(typeof(TargetType))]`, where `TargetType` is the type which is being serialized, and implement the 3 serialization routines. The rules for how to write those routines are identical to method 1. Orleans uses the `[SerializerAttribute(typeof(TargetType))]` to determine that this class is a serializer for `TargetType` and this attribute can be specified multiple times on the same class if it's able to serialize multiple types. Below is an example for such a class:

``` csharp
public class User
{
    public User BestFriend { get; set; }
    public string NickName { get; set; }
    public int FavoriteNumber { get; set; }
    public DateTimeOffset BirthDate { get; set; }
}

[Orleans.CodeGeneration.SerializerAttribute(typeof(User))]
internal class UserSerializer
{
    [CopierMethod]
    public static object DeepCopier(object original, ICopyContext context)
    {
        var input = (User) original;
        var result = new User();

        // Record 'result' as a copy of 'input'. Doing this immediately after construction allows for
        // data structures which have cyclic references or duplicate references.
        // For example, imagine that 'input.BestFriend' is set to 'input'. In that case, failing to record
        // the copy before trying to copy the 'BestFriend' field would result in infinite recursion.
        context.RecordCopy(original, result);

        // Deep-copy each of the fields.
        result.BestFriend = (User)context.SerializationManager.DeepCopy(input.BestFriend);
        result.NickName = input.NickName; // strings in .NET are immutable, so they can be shallow-copied.
        result.FavoriteNumber = input.FavoriteNumber; // ints are primitive value types, so they can be shallow-copied.
        result.BirthDate = (DateTimeOffset)context.SerializationManager.DeepCopy(input.BirthDate);
                
        return result;
    }

    [SerializerMethod]
    public static void Serializer(object untypedInput, ISerializationContext context, Type expected)
    {
        var input = (User) untypedInput;

        // Serialize each field.
        SerializationManager.SerializeInner(input.BestFriend, context);
        SerializationManager.SerializeInner(input.NickName, context);
        SerializationManager.SerializeInner(input.FavoriteNumber, context);
        SerializationManager.SerializeInner(input.BirthDate, context);
    }

    [DeserializerMethod]
    public static object Deserializer(Type expected, IDeserializationContext context)
    {
        var result = new User();

        // Record 'result' immediately after constructing it. As with with the deep copier, this
        // allows for cyclic references and de-duplication.
        context.RecordObject(result);

        // Deserialize each field in the order that they were serialized.
        result.BestFriend = SerializationManager.DeserializeInner<User>(context);
        result.NickName = SerializationManager.DeserializeInner<string>(context);
        result.FavoriteNumber = SerializationManager.DeserializeInner<int>(context);
        result.BirthDate = SerializationManager.DeserializeInner<DateTimeOffset>(context);

        return result;
    }
}
```

### Serializing Generic Types
The `TargetType` parameter of `[Serializer(typeof(TargetType))]` can be an open-generic type, for example, `MyGenericType<>`. In that case, the serializer class must have the same generic parameters as the target type. Orleans will create a concrete version of the serializer at runtime for every concrete `MyGenericType<T>` type which is serialized, for example, one for each of `MyGenericType<int>` and `MyGenericType<string>`.

## Hints for Writing Serializers and Deserializers
Often the simplest way to write a serializer/deserializer pair is to serialize by constructing a byte array and writing the array length to the stream, followed by the array itself, and then deserialize by reversing the process. If the array is fixed-length, you can omit it from the stream. This works well when you have a data type that you can represent compactly and that doesn't have sub-objects that might be duplicated (so you don't have to worry about object identity).

Another approach, which is the approach the Orleans runtime takes for collections such as dictionaries, works well for classes with significant and complex internal structure: use instance methods to access the semantic content of the object, serialize that content, and deserialize by setting the semantic contents rather than the complex internal state. In this approach, inner objects are written using SerializeInner and read using DeserializeInner. In this case, it is common to write a custom copier, as well.

If you write a custom serializer, and it winds up looking like a sequence of calls to SerializeInner for each field in the class, you don't need a custom serializer for that class.

# Fallback Serialization

Orleans supports transmission of arbitrary types at runtime and therefore the in-built code generator cannot determine the entire set of types which will be transmitted ahead of time. Additionally, certain types cannot have serializers generated for them because they are inaccessible (for example, `private`) or have fields which are inaccessible (for example, `readonly`). Therefore, there is a need for just-in-time serialization of types which were unexpected or could not have serializers generated ahead-of-time. The serializer responsible for these types is called the *fallback serializer*. Orleans ships with two fallback serializers:
* `Orleans.Serialization.BinaryFormatterSerializer` which uses .NET's [BinaryFormatter](https://msdn.microsoft.com/en-us/library/system.runtime.serialization.formatters.binary.binaryformatter); and
* `Orleans.Serialization.ILBasedSerializer` which emits [CIL](https://en.wikipedia.org/wiki/Common_Intermediate_Language) instructions at runtime to create serializers which leverage Orleans' serialization framework to serialize each field. This means that if an inaccessible type `MyPrivateType` contains a field `MyType` which has a custom serializer, that custom serializer will be used to serialize it.

The fallback serializer can be configured using the `FallbackSerializationProvider` property on both `ClientConfiguration` on the client and `GlobalConfiguration` on the silos.
```C#
var cfg = new ClientConfiguration();
cfg.FallbackSerializationProvider = typeof(FantasticSerializer).GetTypeInfo();
```
```C#
var cfg = new GlobalConfiguration();
cfg.FallbackSerializationProvider = typeof(FantasticSerializer).GetTypeInfo();
```

Alternatively, the fallback serialization provider can be specified in XML configuration:
``` xml
<Messaging>
  <FallbackSerializationProvider type="GreatCompany.FantasticFallbackSerializer, GreatCompany.SerializerAssembly"/>
</Messaging>
```

.NET Core uses the `ILBasedSerializer` by default, whereas .NET 4.6 uses `BinaryFormatterSerializer` by default.

# Optimize Copying Using Immutable Types

Orleans has a feature that can be used to avoid some of the overhead associated with serializing messages containing immutable types. This section describes the feature and its application, starting with context on where it is relevant.

## Serialization in Orleans
When a grain method is invoked, the Orleans runtime makes a deep copy of the method arguments and forms the request out of the copies. This protects against the calling code modifying the argument objects before the data is passed to the called grain.

If the called grain is on a different silo, then the copies are eventually serialized into a byte stream and sent over the network to the target silo, where they are deserialized back into objects. If the called grain is on the same silo, then the copies are handed directly to the called method.

Return values are handled the same way: first copied, then possibly serialized and deserialized.

Note that all 3 processes, copying, serializing, and deserializing, respect object identity. In other words, if you pass a list that has the same object in it twice, on the receiving side you'll get a list with the same object in it twice, rather than with two objects with the same values in them.

## Optimizing Copying
In many cases, the deep copying is unnecessary. For instance, a possible scenario is a web front-end that receives a byte array from its client and passes that request, including the byte array, on to a grain for processing. The front-end process doesn't do anything with the array once it has passed it on to the grain; in particular, it doesn't reuse the array to receive a future request. Inside the grain, the byte array is parsed to fetch the input data, but not modified. The grain returns another byte array that it has created to get passed back to the web client; it discards the array as soon as it returns it. The web front-end passes the result byte array back to its client, without modification.

In such a scenario, there is no need to copy either the request or response byte arrays. Unfortunately, the Orleans runtime can't figure this out by itself, since it can't tell whether or not the arrays are modified later on by the web front-end or by the grain. In the best of all possible worlds, we'd have some sort of .NET mechanism for indicating that a value is no longer modified; lacking that, we've added Orleans-specific mechanisms for this: the `Immutable<T>` wrapper class and the `[Immutable]` attribute.

### Using `Immutable<T>`

The `Orleans.Concurrency.Immutable<T>` wrapper class is used to indicate that a value may be considered immutable; that is, the underlying value will not be modified, so no copying is required for safe sharing. Note that using `Immutable<T>` implies that neither the provider of the value nor the recipient of the value will modify it in the future; it is not a one-sided commitment, but rather a mutual dual-side commitment.

Using `Immutable<T>` is simple: in your grain interface, instead of passing `T`, pass `Immutable<T>`. For instance, in the above described scenario, the grain method that was:

``` csharp
Task<byte[]> ProcessRequest(byte[] request);
```

Becomes:

``` csharp
Task<Immutable<byte[]>> ProcessRequest(Immutable<byte[]> request);
```

To create an `Immutable<T>`, simply use the constructor:

``` csharp
Immutable<byte[]> immutable = new Immutable<byte[]>(buffer);
```

To get the value inside the immutable, use the `.Value` property:

``` csharp
byte[] buffer = immutable.Value;
```

### Using `[Immutable]`
For user-defined types, the `[Orleans.Concurrency.Immutable]` attribute can be added to the type. This instructs Orleans' serializer to avoid copying instances of this type.
The following code snippet demonstrates using `[Immutable]` to denote an immutable type. This type will not be copied during transmission.
``` csharp
[Immutable]
public class MyImmutableType
{
    public MyImmutableType(int value)
    {
        this.MyValue = value;
    }

    public int MyValue { get; }
}
```

## Immutability in Orleans

For Orleans' purposes, immutability is a rather strict statement: the contents of the data item will not be modified in any way that could change the item's semantic meaning, or that would interfere with another thread simultaneously accessing the item. The safest way to ensure this is to simply not modify the item at all: bitwise immutability, rather than logical immutability.

In some cases it is safe to relax this to logical immutability, but care must be taken to ensure that the mutating code is properly thread-safe; because dealing with multithreading is complex, and uncommon in an Orleans context, we strongly recommend against this approach and recommend sticking to bitwise immutability.

# Serialization Best Practices
Serialization serves two primary purposes in Orleans:
1. As a wire format for transmitting data between grains and clients at runtime.
2. As a storage format for persisting long-lived data for later retrieval.

The serializers generated by Orleans are suitable for the first purpose due to their flexibility, performance, and versatility. They are not as suitable for the second purpose, since they are not explicitly version-tolerant. It is recommended that users configure a version-tolerant serializer such as [Protocol Buffers](https://developers.google.com/protocol-buffers/) for persistent data. Protocol Buffers is supported via `Orleans.Serialization.ProtobufSerializer` from the [Microsoft.Orleans.OrleansGoogleUtils](https://www.nuget.org/packages/Microsoft.Orleans.OrleansGoogleUtils/) NuGet package. The best-practices for the particular serializer of choice should be used in order to ensure version-tolerance. Third-party serializers can be configured using the `SerializationProviders` configuration property as described above.
