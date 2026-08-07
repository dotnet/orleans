---
title: Serialization in Orleans
description: Learn about serialization and custom serializers in .NET Orleans.
ms.date: 05/23/2025
ms.topic: overview
uid: orleans-serialization
---

# Serialization in Orleans

There are broadly two kinds of serialization used in Orleans:

- **Grain call serialization**: Used to serialize objects passed to and from grains.
- **Grain storage serialization**: Used to serialize objects to and from storage systems.

Most of this article focuses on grain call serialization via the serialization framework included in Orleans. The [Grain storage serializers](#grain-storage-serializers) section discusses grain storage serialization.

For the generated-code pipeline, runtime manifests, wire identity, and codec dispatch internals, see [Serialization and code generation internals](../../implementation/serialization.md).

## Use Orleans serialization

Orleans includes an advanced and extensible serialization framework referred to as **Orleans.Serialization**. The serialization framework included in Orleans is designed to meet the following goals:

- **High-performance**: The serializer is designed and optimized for performance. More details are available in [this presentation](https://www.youtube.com/watch?v=kgRag4E6b4c).
- **High-fidelity**: The serializer faithfully represents most of .NET's type system, including support for generics, polymorphism, inheritance hierarchies, object identity, and cyclic graphs. Pointers aren't supported since they aren't portable across processes.
- **Flexibility**: You can customize the serializer to support third-party libraries by creating [*surrogates*](#surrogates-for-serializing-foreign-types) or delegating to external serialization libraries such as **System.Text.Json**, **Newtonsoft.Json**, **MessagePack**, and **Google.Protobuf**.
- **Version-tolerance**: The serializer allows application types to evolve over time, supporting:
  - Adding and removing members
  - Subclassing
  - Numeric widening and narrowing (e.g., `int` to/from `long`, `float` to/from `double`)
  - Renaming types

High-fidelity representation of types is fairly uncommon for serializers, so some points warrant further explanation:

1. **Dynamic types and arbitrary polymorphism**: Orleans doesn't enforce restrictions on the types passed in grain calls and maintains the dynamic nature of the actual data type. This means, for example, if a method in a grain interface is declared to accept <xref:System.Collections.IDictionary>, but at runtime the sender passes a <xref:System.Collections.Generic.SortedDictionary`2>, the receiver indeed gets a `SortedDictionary` (even though the "static contract"/grain interface didn't specify this behavior).

1. **Maintaining object identity**: If the same object is passed multiple times in the arguments of a grain call or is indirectly pointed to more than once from the arguments, Orleans serializes it only once. On the receiver side, Orleans restores all references correctly so that two pointers to the same object still point to the same object after deserialization. Preserving object identity is important in scenarios like the following: Imagine grain A sends a dictionary with 100 entries to grain B, and 10 keys in the dictionary point to the same object, `obj`, on A's side. Without preserving object identity, B would receive a dictionary of 100 entries with those 10 keys pointing to 10 different clones of `obj`. With object identity preserved, the dictionary on B's side looks exactly like on A's side, with those 10 keys pointing to a single object `obj`. Note that because default string hash code implementations in .NET are randomized per process, the ordering of values in dictionaries and hash sets (for example) might not be preserved.

To support version tolerance, the serializer requires you to be explicit about which types and members are serialized. We've tried to make this as painless as possible. Mark all serializable types with <xref:Orleans.GenerateSerializerAttribute?displayProperty=nameWithType> to instruct Orleans to generate serializer code for your type. Once you've done this, you can use the included code fix to add the required <xref:Orleans.IdAttribute?displayProperty=nameWithType> to the serializable members on your types, as demonstrated here:

:::image type="content" source="media/generate-serializer-code-fix.gif" alt-text="An animated image of the available code fix being suggested and applied on the GenerateSerializerAttribute when the containing type doesn't contain IdAttribute's on its members." lightbox="media/generate-serializer-code-fix.gif":::

Here is an example of a serializable type in Orleans, demonstrating how to apply the attributes.

:::code language="csharp" source="snippets/serialization/BasicTypes.cs" id="basic_employee_class":::

Orleans supports inheritance and serializes the individual layers in the hierarchy separately, allowing them to have distinct member IDs.

:::code language="csharp" source="snippets/serialization/BasicTypes.cs" id="inheritance_publication_book":::

In the preceding code, note that both `Publication` and `Book` have members with `[Id(0)]`, even though `Book` derives from `Publication`. This is the recommended practice in Orleans because member identifiers are scoped to the inheritance level, not the type as a whole. You can add and remove members from `Publication` and `Book` independently, but you cannot insert a new base class into the hierarchy once the application is deployed without special consideration.

Orleans also supports serializing types with `internal`, `private`, and `readonly` members, such as in this example type:

:::code language="csharp" source="snippets/serialization/BasicTypes.cs" id="custom_struct_private_readonly":::

By default, Orleans serializes your type by encoding its full name. You can override this by adding an <xref:Orleans.AliasAttribute?displayProperty=nameWithType>. Doing so results in your type being serialized using a name resilient to renaming the underlying class or moving it between assemblies. Type aliases are globally scoped, and you cannot have two aliases with the same value in an application. For generic types, the alias value must include the number of generic parameters preceded by a backtick; for example, `MyGenericType<T, U>` could have the alias <code>[Alias("mytype\`2")]</code>.

## Serializing `record` types

Members defined in a record's primary constructor have implicit IDs by default. In other words, Orleans supports serializing `record` types. This means you cannot change the parameter order for an already deployed type, as that breaks compatibility with previous versions of your application (in a rolling upgrade scenario) and with serialized instances of that type in storage and streams. Members defined in the body of a record type don't share identities with the primary constructor parameters.

:::code language="csharp" source="snippets/serialization/BasicTypes.cs" id="record_primary_constructor":::

If you don't want the primary constructor parameters automatically included as serializable fields, use `[GenerateSerializer(IncludePrimaryConstructorParameters = false)]`.

## MessagePack serialization

You can use [MessagePack](https://github.com/neuecc/MessagePack-CSharp) as an external serializer for Orleans. MessagePack is a high-performance binary serialization format that produces smaller payloads than JSON while maintaining fast serialization and deserialization speeds.

### When to use MessagePack

Consider using MessagePack when:

- You need interoperability with non-.NET systems that support MessagePack
- You have types already annotated with MessagePack attributes (`[MessagePackObject]`, `[Key]`)
- You want smaller payload sizes compared to JSON-based serializers
- You need a standardized binary format

For most Orleans applications, the default Orleans serializer is recommended because it provides higher fidelity (.NET type system support), object identity preservation, and automatic serializer generation.

### Install the MessagePack serializer package

Add the MessagePack serializer package to your project:

```dotnetcli
dotnet add package Microsoft.Orleans.Serialization.MessagePack
```

### Configure MessagePack serialization

To configure MessagePack serialization, use the `AddMessagePackSerializer` extension method:

:::code source="./snippets/serialization/MessagePackExamples.cs" id="messagepack_basic_config":::

The `isSerializable` delegate controls which types are handled by the MessagePack serializer. Types not matching this predicate fall back to the default Orleans serializer.

### MessagePackCodecOptions

You can configure the MessagePack serializer using `MessagePackCodecOptions`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SerializerOptions` | `MessagePackSerializerOptions` | `MessagePackSerializerOptions.Standard` | The MessagePack serializer options to use for serialization. |
| `AllowDataContractAttributes` | `bool` | `false` | When `true`, allows types marked with `[DataContract]` to be serialized using MessagePack. |
| `IsSerializableType` | `Func<Type, bool?>` | `null` | A delegate to determine if a type should be serialized by MessagePack. |
| `IsCopyableType` | `Func<Type, bool?>` | `null` | A delegate to determine if a type should be copied by MessagePack. |

### Example: Configure with options

:::code source="./snippets/serialization/MessagePackExamples.cs" id="messagepack_with_options":::

### Define MessagePack types

Types serialized by MessagePack should use MessagePack attributes:

:::code source="./snippets/serialization/MessagePackExamples.cs" id="messagepack_type_definition":::

You can then use these types in grain interfaces:

:::code source="./snippets/serialization/MessagePackExamples.cs" id="messagepack_grain_interface":::

### Serializer comparison

| Feature | Orleans Native | MessagePack | System.Text.Json |
|---------|----------------|-------------|------------------|
| Format | Binary | Binary | Text (JSON) |
| .NET type fidelity | Excellent | Good | Limited |
| Object identity | Yes | No | No |
| Payload size | Small | Smallest | Largest |
| Cross-platform | .NET only | Any MessagePack client | Any JSON client |
| Version tolerance | Yes | Yes | Yes |
| Setup required | None | Explicit attributes | Explicit attributes |

## Surrogates for serializing foreign types

Sometimes, you might need to pass types between grains over which you don't have full control. In these cases, manually converting to and from a custom-defined type in your application code might be impractical. Orleans offers a solution for these situations: surrogate types. Surrogates are serialized in place of their target type and have functionality to convert to and from the target type. Consider the following example of a foreign type and a corresponding surrogate and converter:

:::code source="./snippets/serialization/SurrogateExamples.cs" id="surrogate_value_type":::

In the preceding code:

- `MyForeignLibraryValueType` is a type outside your control, defined in a consuming library.
- `MyForeignLibraryValueTypeSurrogate` is a surrogate type mapping to `MyForeignLibraryValueType`.
- <xref:Orleans.RegisterConverterAttribute> specifies that `MyForeignLibraryValueTypeSurrogateConverter` acts as a converter to map between the two types. The class implements the <xref:Orleans.IConverter`2> interface.

Orleans supports serialization of types in type hierarchies (types deriving from other types). If a foreign type might appear in a type hierarchy (e.g., as the base class for one of your own types), you must additionally implement the <xref:Orleans.IPopulator`2?displayProperty=nameWithType> interface. Consider the following example:

:::code source="./snippets/serialization/SurrogateExamples.cs" id="surrogate_class_with_populator":::

## Versioning rules

Version tolerance is supported provided you follow a set of rules when modifying types. If you're familiar with systems like Google Protocol Buffers (Protobuf), these rules will be familiar.

### Compound types (`class` & `struct`)

- Inheritance is supported, but modifying the inheritance hierarchy of an object isn't supported. You cannot add, change, or remove the base class of a class.
- With the exception of some numeric types described in the *Numerics* section below, you cannot change field types.
- You can add or remove fields at any point in an inheritance hierarchy.
- You cannot change field IDs.
- Field IDs must be unique for each level in a type hierarchy but can be reused between base classes and subclasses. For example, a `Base` class can declare a field with ID `0`, and a `Sub : Base` class can declare a different field with the same ID, `0`.

### Numerics

- You cannot change the *signedness* of a numeric field.
  - Conversions between `int` & `uint` are invalid.
- You can change the *width* of a numeric field.
  - E.g., conversions from `int` to `long` or `ulong` to `ushort` are supported.
  - Conversions narrowing the width throw an exception if the field's runtime value causes an overflow.
  - Conversion from `ulong` to `ushort` is only supported if the runtime value is less than `ushort.MaxValue`.
  - Conversions from `double` to `float` are only supported if the runtime value is between `float.MinValue` and `float.MaxValue`.
  - Similarly for `decimal`, which has a narrower range than both `double` and `float`.

## Copiers

Orleans promotes safety by default, including safety from some classes of concurrency bugs. In particular, Orleans immediately copies objects passed in grain calls by default. Orleans.Serialization facilitates this copying. When you apply <xref:Orleans.GenerateSerializerAttribute?displayProperty=nameWithType> to a type, Orleans also generates copiers for that type. Orleans avoids copying types or individual members marked with <xref:Orleans.ImmutableAttribute>. For more details, see [Serialization of immutable types in Orleans](./serialization-immutability.md).

## Serialization best practices

- ✅ **Do** give your types aliases using the `[Alias("my-type")]` attribute. Types with aliases can be renamed without breaking compatibility.
- ❌ **Do not** change a `record` to a regular `class` or vice-versa. Records and classes aren't represented identically since records have primary constructor members in addition to regular members; therefore, the two aren't interchangeable.
- ❌ **Do not** add new types to an existing type hierarchy for a serializable type. You must not add a new base class to an existing type. You can safely add a new subclass to an existing type.
- ✅ **Do** replace usages of <xref:System.SerializableAttribute> with <xref:Orleans.GenerateSerializerAttribute> and corresponding <xref:Orleans.IdAttribute> declarations.
- ✅ **Do** start all member IDs at zero for each type. IDs in a subclass and its base class can safely overlap. Both properties in the following example have IDs equal to `0`.

    :::code language="csharp" source="snippets/serialization/BasicTypes.cs" id="best_practices_id_overlap":::

- ✅ **Do** widen numeric member types as needed. You can widen `sbyte` to `short` to `int` to `long`.
  - You can narrow numeric member types, but it results in a runtime exception if observed values cannot be represented correctly by the narrowed type. For example, `int.MaxValue` cannot be represented by a `short` field, so narrowing an `int` field to `short` can result in a runtime exception if such a value is encountered.
- ❌ **Do not** change the signedness of a numeric type member. You must not change a member's type from `uint` to `int` or `int` to `uint`, for example.

## Grain storage serializers

Orleans includes a provider-backed persistence model for grains, accessed via the <xref:Orleans.Grain`1.State?displayName=nameWithType> property or by injecting one or more <xref:Orleans.Runtime.IPersistentState`1> values into your grain. The general-purpose <xref:Orleans.Storage.IGrainStorageSerializer> interface offers a consistent way to customize state serialization for each provider. Supported storage providers implement a pattern involving setting the <xref:Orleans.Storage.IStorageProviderSerializerOptions.GrainStorageSerializer?displayProperty=nameWithType> property on the provider's options class, for example:

- <xref:Orleans.Configuration.DynamoDBStorageOptions.GrainStorageSerializer?displayProperty=nameWithType>
- <xref:Orleans.Configuration.AzureBlobStorageOptions.GrainStorageSerializer?displayProperty=nameWithType>
- <xref:Orleans.Configuration.AzureTableStorageOptions.GrainStorageSerializer?displayProperty=nameWithType>
- <xref:Orleans.Configuration.AdoNetGrainStorageOptions.GrainStorageSerializer>

Grain storage serialization currently defaults to `Newtonsoft.Json` to serialize state. You can replace this by modifying that property at configuration time. The following example demonstrates this using [OptionsBuilder\<TOptions\>](https://learn.microsoft.com/dotnet/core/extensions/options#optionsbuilder-api):

:::code language="csharp" source="snippets/serialization/GrainStorageExamples.cs" id="grain_storage_serializer_config":::

For more information, see [OptionsBuilder API](https://learn.microsoft.com/dotnet/core/extensions/options#optionsbuilder-api).
