---
title: Serialization customization in Orleans
description: Learn how to customize serialization in .NET Orleans.
ms.date: 07/03/2024
uid: orleans-serialization-customization
zone_pivot_groups: orleans-version
---

# Serialization customization in Orleans

One important aspect of Orleans is its support for customization of serialization, which is the process of converting an object or data structure into a format that can be stored or transmitted, and reconstructed later. This allows developers to control how data is encoded and decoded when it is sent between different parts of the system. Serialization customization can be useful for optimizing performance, interoperability, and security.

## Serialization providers

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0,orleans-7-0"

Orleans provides two serializer implementations:

- [Microsoft.Orleans.Serialization.SystemTextJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.SystemTextJson)
- [Microsoft.Orleans.Serialization.NewtonsoftJson](https://nuget.org/packages/Microsoft.Orleans.Serialization.NewtonsoftJson)

To configure either of these packages, see [Serialization configuration in Orleans](serialization-configuration.md?pivots=orleans-7-0).

## Custom serializer implementation

To create a custom serializer implementation, there are a few common steps involved. You have to implement several interfaces and then register your serializer with the Orleans runtime. The following sections describe the steps in more detail.

Start by implementing the following Orleans serialization interfaces:

- <xref:Orleans.Serialization.Serializers.IGeneralizedCodec>: A codec which supports multiple types.
- <xref:Orleans.Serialization.Cloning.IGeneralizedCopier>: Provides functionality for copying objects of multiple types.
- <xref:Orleans.Serialization.ITypeFilter>: Functionality for allowing types to be loaded and to participate in serialization and deserialization.

Consider the following example of a custom serializer implementation:

```csharp
internal sealed class CustomOrleansSerializer :
    IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    void IFieldCodec.WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        object value) =>
        throw new NotImplementedException();

    object IFieldCodec.ReadValue<TInput>(
        ref Reader<TInput> reader, Field field) =>
        throw new NotImplementedException();

    bool IGeneralizedCodec.IsSupportedType(Type type) =>
        throw new NotImplementedException();

    object IDeepCopier.DeepCopy(object input, CopyContext context) =>
        throw new NotImplementedException();

    bool IGeneralizedCopier.IsSupportedType(Type type) =>
        throw new NotImplementedException();
}
```

In the preceding example implementation:

- Each interface is explicitly implemented to avoid conflicts with method name resolution.
- Each method throws a <xref:System.NotImplementedException> to indicate that the method is not implemented. You'll need to implement each method to provide the desired functionality.

The next step is to register your serializer with the Orleans runtime. This is typically achieved by extending <xref:Orleans.Serialization.ISerializerBuilder> and exposing a custom `AddCustomSerializer` extension method. The following example demonstrates the typical pattern:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Cloning;

public static class SerializationHostingExtensions
{
    public static ISerializerBuilder AddCustomSerializer(
        this ISerializerBuilder builder)
    {
        var services = builder.Services;

        services.AddSingleton<CustomOrleansSerializer>();
        services.AddSingleton<IGeneralizedCodec, CustomOrleansSerializer>();
        services.AddSingleton<IGeneralizedCopier, CustomOrleansSerializer>();
        services.AddSingleton<ITypeFilter, CustomOrleansSerializer>();

        return builder;
    }
}
```

Additional considerations would be to expose an overload that accepts custom serialization options specific to your custom implementation. These options could be configured along with the registration in the builder. These options could be dependency injected into your custom serializer implementation.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Orleans supports integration with third-party serializers using a provider model. This requires an implementation of the <xref:Orleans.Serialization.IExternalSerializer> type described in the custom serialization section of this article. Integrations for some common serializers are maintained alongside Orleans, for example:

- [Protocol Buffers](https://developers.google.com/protocol-buffers/): <xref:Orleans.Serialization.ProtobufSerializer?displayProperty=fullName> from the [Microsoft.Orleans.OrleansGoogleUtils](https://www.nuget.org/packages/Microsoft.Orleans.OrleansGoogleUtils/) NuGet package.
- [Bond](https://github.com/microsoft/bond/): <xref:Orleans.Serialization.BondSerializer?displayProperty=fullName> from the [Microsoft.Orleans.Serialization.Bond](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.Bond/) NuGet package.
- [Newtonsoft.Json](https://www.newtonsoft.com/json): <xref:Orleans.Serialization.OrleansJsonSerializer?displayProperty=fullName> from the core Orleans library.

Custom implementation of `IExternalSerializer` is described in the following section.

## Custom external serializers

In addition to automatic serialization generation, app code can provide custom serialization for the types it chooses. Orleans recommends using the automatic serialization generation for the majority of your app types and only writing custom serializers in rare cases when you believe it is possible to get improved performance by hand-coding serializers. This note describes how to do so and identifies some specific cases when it might be helpful.

There are three ways in which apps can customize serialization:

1. Add serialization methods to your type and mark them with appropriate attributes (<xref:Orleans.CodeGeneration.CopierMethodAttribute>, <xref:Orleans.CodeGeneration.SerializerMethodAttribute>, <xref:Orleans.CodeGeneration.DeserializerMethodAttribute>). This method is preferable for types that your app owns, that is, the types that you can add new methods to.
1. Implement `IExternalSerializer` and register it during configuration time. This method is useful for integrating an external serialization library.
1. Write a separate static class annotated with an `[Serializer(typeof(YourType))]` with the 3 serialization methods in it and the same attributes as above. This method is useful for types that the app does not own, for example, types defined in other libraries your app has no control over.

Each of these serialization methods is detailed in the following sections.

### Custom serialization introduction

Orleans serialization happens in three stages:

- Objects are immediately deep copied to ensure isolation.
- Before being put on the wire, objects are serialized to a message byte stream.
- When delivered to the target activation, objects are recreated (deserialized) from the received byte stream.

Data types that may be sent in messages&mdash;that is, types that may be passed as method arguments or return values&mdash;must have associated routines that perform these three steps. We refer to these routines collectively as the serializers for a data type.

The copier for a type stands alone, while the serializer and deserializer are a pair that work together. You can provide just a custom copier, or just a custom serializer and a custom deserializer, or you can provide custom implementations of all three.

Serializers are registered for each supported data type at silo start-up and whenever an assembly is loaded. Registration is necessary for custom serializer routines for a type to be used. Serializer selection is based on the dynamic type of the object to be copied or serialized. For this reason, there is no need to create serializers for abstract classes or interfaces, because they will never be used.

### When to write a custom serializer

A hand-crafted serializer routine will rarely perform better than the generated versions. If you are tempted to write one, you should first consider the following options:

- If there are fields or properties within your data types that don't have to be serialized or copied, you can mark them with the <xref:System.NonSerializedAttribute>. This will cause the generated code to skip these fields when copying and serializing. Use <xref:Orleans.Concurrency.ImmutableAttribute> and <xref:Orleans.Concurrency.Immutable`1> where possible to avoid copying immutable data. For more information, see [Optimize copying](serialization-immutability.md#optimize-copying). If you're avoiding using the standard generic collection types, don't. The Orleans runtime contains custom serializers for the generic collections that use the semantics of the collections to optimize copying, serializing, and deserializing. These collections also have special "abbreviated" representations in the serialized byte stream, resulting in even more performance advantages. For instance, a `Dictionary<string, string>` will be faster than a `List<Tuple<string, string>>`.

- The most common case where a custom serializer can provide a noticeable performance gain is when there is significant semantic information encoded in the data type that is not available by simply copying field values. For instance, arrays that are sparsely populated may often be more efficiently serialized by treating the array as a collection of index/value pairs, even if the app keeps the data as a fully realized array for speed of operation.

- A key thing to do before writing a custom serializer is to make sure that the generated serializer is hurting your performance. Profiling will help a bit here, but even more valuable is running end-to-end stress tests of your app with varying serialization loads to gauge the system-level impact, rather than the micro-impact of serialization. For instance, building a test version that passes no parameters to or results from grain methods, simply using canned values at either end, will zoom in on the impact of serialization and copying on system performance.

### Add serialization methods to a type

All serializer routines should be implemented as static members of the class or struct they operate on. The names shown here are not required; registration is based on the presence of the respective attributes, not on method names. Note that serializer methods need not be public.

Unless you implement all three serialization routines, you should mark your type with the <xref:System.SerializableAttribute> so that the missing methods will be generated for you.

#### Copier

Copier methods are flagged with the <xref:Orleans.CodeGeneration.CopierMethodAttribute?displayProperty=fullName>:

```csharp
[CopierMethod]
static private object Copy(object input, ICopyContext context)
{
    // ...
}
```

Copiers are usually the simplest serializer routines to write. They take an object, guaranteed to be of the same type as the type the copier is defined in, and must return a semantically-equivalent copy of the object.

If, as part of copying the object, a sub-object needs to be copied, the best way to do so is to use the <xref:Orleans.Serialization.SerializationManager.DeepCopyInner*?displayProperty=nameWithType> routine:

```csharp
var fooCopy = SerializationManager.DeepCopyInner(foo, context);
```

> [!IMPORTANT]
> It is important to use <xref:Orleans.Serialization.SerializationManager.DeepCopyInner*?displayProperty=nameWithType>, instead of <xref:Orleans.Serialization.SerializationManager.DeepCopy*?displayProperty=nameWithType>, to maintain the object identity context for the full copy operation.

#### Maintain object identity

An important responsibility of a copy routine is to maintain object identity. The Orleans runtime provides a helper class for this purpose. Before copying a sub-object "by hand" (not by calling `DeepCopyInner`), check to see if it has already been referenced as follows:

```csharp
var fooCopy = context.CheckObjectWhileCopying(foo);
if (fooCopy is null)
{
    // Actually make a copy of foo
    context.RecordObject(foo, fooCopy);
}
```

The last line is the call to <xref:Orleans.Serialization.SerializationContext.RecordObject*>, which is required so that possible future references to the same object as `foo` references will get found properly by <xref:Orleans.Serialization.SerializationContext.CheckObjectWhileCopying*>.

> [!NOTE]
> This should only be done for class instances, _not_ `struct` instances or .NET primitives such as `string`, `Uri`, and `enum`.

If you use `DeepCopyInner` to copy sub-objects, then object identity is handled for you.

### Serializer

Serialization methods are flagged with the <xref:Orleans.CodeGeneration.SerializerMethodAttribute?displayProperty=fullName>:

```csharp
[SerializerMethod]
static private void Serialize(
    object input,
    ISerializationContext context,
    Type expected)
{
    // ...
}
```

As with copiers, the "input" object passed to a serializer is guaranteed to be an instance of the defining type. The "expected" type may be ignored; it is based on compile-time type information about the data item, and is used at a higher level to form the type prefix in the byte stream.

To serialize sub-objects, use the <xref:Orleans.Serialization.SerializationManager.SerializeInner*?displayProperty=nameWithType> routine:

```csharp
SerializationManager.SerializeInner(foo, context, typeof(FooType));
```

If there is no particular expected type for foo, then you can pass null for the expected type.

The <xref:Orleans.Serialization.BinaryTokenStreamWriter> class provides a wide variety of methods for writing data to the byte stream. An instance of the class can be obtained via the `context.StreamWriter` property. See the class for documentation.

### Deserializer

Deserialization methods are flagged with the <xref:Orleans.CodeGeneration.DeserializerMethodAttribute?displayProperty=fullName>:

```csharp
[DeserializerMethod]
static private object Deserialize(
    Type expected,
    IDeserializationContext context)
{
    //...
}
```

The "expected" type may be ignored; it is based on compile-time type information about the data item and is used at a higher level to form the type prefix in the byte stream. The actual type of the object to be created will always be the type of class in which the deserializer is defined.

To deserialize sub-objects, use the <xref:Orleans.Serialization.SerializationManager.DeserializeInner*?displayProperty=nameWithType> routine:

```csharp
var foo = SerializationManager.DeserializeInner(typeof(FooType), context);
```

Or, alternatively:

```csharp
var foo = SerializationManager.DeserializeInner<FooType>(context);
```

If there is no particular expected type for foo, use the non-generic `DeserializeInner` variant and pass `null` for the expected type.

The <xref:Orleans.Serialization.BinaryTokenStreamReader> class provides a wide variety of methods for reading data from the byte stream. An instance of the class can be obtained via the `context.StreamReader` property. See the class for documentation.

## Write a serializer provider

In this method, you implement <xref:Orleans.Serialization.IExternalSerializer?displayProperty=fullName> and add it to the <xref:Orleans.Configuration.SerializationProviderOptions.SerializationProviders?displayProperty=nameWithType> property on both <xref:Orleans.Runtime.Configuration.ClientConfiguration> on the client and <xref:Orleans.Runtime.Configuration.GlobalConfiguration> on the silos. For information on configuration, see [Serialization providers](#serialization-providers).

Implementations of `IExternalSerializer` follows the pattern previously described for serialization with the addition of an `Initialize` method and an `IsSupportedType` method which Orleans uses to determine if the serializer supports a given type. This is the interface definition:

```csharp
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

## Write a serializer for an individual type

In this method, you write a new class annotated with an attribute `[SerializerAttribute(typeof(TargetType))]`, where `TargetType` is the type that is being serialized, and implement the 3 serialization routines. The rules for how to write those routines are identical to that when implementing the `IExternalSerializer`. Orleans uses the `[SerializerAttribute(typeof(TargetType))]` to determine that this class is a serializer for `TargetType` and this attribute can be specified multiple times on the same class if it's able to serialize multiple types. Below is an example for such a class:

```csharp
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
    public static object DeepCopier(
        object original, ICopyContext context)
    {
        var input = (User)original;
        var result = new User();

        // Record 'result' as a copy of 'input'. Doing this
        // immediately after construction allows for data
        // structures that have cyclic references or duplicate
        // references. For example, imagine that 'input.BestFriend'
        // is set to 'input'. In that case, failing to record
        // the copy before trying to copy the 'BestFriend' field
        // would result in infinite recursion.
        context.RecordCopy(original, result);

        // Deep-copy each of the fields.
        result.BestFriend =
            (User)context.SerializationManager.DeepCopy(input.BestFriend);

        // strings in .NET are immutable, so they can be shallow-copied.
        result.NickName = input.NickName;
        // ints are primitive value types, so they can be shallow-copied.
        result.FavoriteNumber = input.FavoriteNumber;
        result.BirthDate =
            (DateTimeOffset)context.SerializationManager.DeepCopy(input.BirthDate);

        return result;
    }

    [SerializerMethod]
    public static void Serializer(
        object untypedInput, ISerializationContext context, Type expected)
    {
        var input = (User) untypedInput;

        // Serialize each field.
        SerializationManager.SerializeInner(input.BestFriend, context);
        SerializationManager.SerializeInner(input.NickName, context);
        SerializationManager.SerializeInner(input.FavoriteNumber, context);
        SerializationManager.SerializeInner(input.BirthDate, context);
    }

    [DeserializerMethod]
    public static object Deserializer(
        Type expected, IDeserializationContext context)
    {
        var result = new User();

        // Record 'result' immediately after constructing it.
        // As with the deep copier, this
        // allows for cyclic references and de-duplication.
        context.RecordObject(result);

        // Deserialize each field in the order that they were serialized.
        result.BestFriend =
            SerializationManager.DeserializeInner<User>(context);
        result.NickName =
            SerializationManager.DeserializeInner<string>(context);
        result.FavoriteNumber =
            SerializationManager.DeserializeInner<int>(context);
        result.BirthDate =
            SerializationManager.DeserializeInner<DateTimeOffset>(context);

        return result;
    }
}
```

### Serialize generic types

The `TargetType` parameter of `[Serializer(typeof(TargetType))]` can be an open-generic type, for example, `MyGenericType<T>`. In that case, the serializer class must have the same generic parameters as the target type. Orleans will create a concrete version of the serializer at runtime for every concrete `MyGenericType<T>` type which is serialized, for example, one for each of `MyGenericType<int>` and `MyGenericType<string>`.

## Hints for writing serializers and deserializers

Often the simplest way to write a serializer/deserializer pair is to serialize by constructing a byte array and writing the array length to the stream, followed by the array itself, and then deserialize by reversing the process. If the array is fixed-length, you can omit it from the stream. This works well when you have a data type that you can represent compactly and that doesn't have sub-objects that might be duplicated (so you don't have to worry about object identity).

Another approach, which is the approach the Orleans runtime takes for collections such as dictionaries, works well for classes with significant and complex internal structure: use instance methods to access the semantic content of the object, serialize that content, and deserialize by setting the semantic contents rather than the complex internal state. In this approach, inner objects are written using SerializeInner and read using DeserializeInner. In this case, it is common to write a custom copier, as well.

If you write a custom serializer, and it winds up looking like a sequence of calls to SerializeInner for each field in the class, you don't need a custom serializer for that class.

:::zone-end

## See also

- [Serialization overview in Orleans](serialization.md)
- [Serialization configuration in Orleans](serialization-configuration.md)
