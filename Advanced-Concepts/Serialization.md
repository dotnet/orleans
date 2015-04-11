---
layout: page
title: Serialization and Writing Custom Serializers
---
{% include JB/setup %}

# Orleans Serialization

Orleans has an advance and extensible serialization framework. Orleans serializes data types passed in grain request and response messages as well as grain persistent state objects. As part of this framework, Orleans automaticaly generates serialization code for those data types. Orleans serialization is compatible with the standard .NET binary serialization but is more effecient and provides more compact representation. In addition to generating a more efficient serialization/deserialization for types that are already .NET-serializable Orleans also tries to generate serializers for types used in grain interfaces that are not .NET-serializable. The framework also includes a set of efficient built-in serializers for frequently used types: lists, dictionaries, strings, primitives, arrays, etc.

There are 2 important features of Orleans's serializer that set it apart from a lot of other third party serialization frameworks: dynamic types/arbitrary polymorphism and object identity.

1. **Dynamic types and arbitrary polymorphism** - Orleans does not put any restrictions on the types that can be passed in grain calls and maintains the dynamic nature of the actual data type. That means, for example, that if the method in the grain interfaces is declared to accept `IDictionary` but at runtime the sender passes `SortedDictionary`, the receiver will indeed get `SortedDictionary` (although the "static contract"/grain interface did not specify this behaviour). 

2. **Maintaining Object identity** - if the same object is passed multiple types in the arguments of a grain call or is indirectly pointed more than once from the arguments, Orleans will serialize it only once. At the receiver side Orleans will restore all references correctly, so that two pointers to the same object still point to the same object after deserialization as well. Object identity is important to preserve in scenarios like the following. Imagine actor A is sending a dictionary with 100 entries to actor B, and 10 of the keys in the dictionary point to the same object obj on the A's side. Without preserving object identity, B would receive a dictionary of 100 entries with those 10 keys pointing to 10 different clones of obj. With object identity preserved, the dictionary on B's side looks exactly like on A's side with those 10 keys pointing to a single object obj.

The above two behaviours are provided by the standard .NET binary serializer and it was therefore important for us to support this standard and familiar behaviour in Orleans as well.



# Writing Custom Serializers

In addition to automatic serialization generation, application code can provide custom serialization for types it chooses. Orleans reccomends using the automatic serialization generation for the majority of your application types and only write custom serializers in rare cases when you beleive it is possible to get improved performance by hand-coding serializers. This note describes how to do so, and identifies some specific cases when it might be helpful.

There are 2 ways how application can customise serialization:

1) Add 3 serialization methods to your type and mark them with appropriate attributes (`CopierMethod`, `SerializerMethod`, `DeserializerMethod`). This method is preferable for types that application owns, that is, the types that you can add new methods to. 

2) Write a separate class annotated with an attribute `RegisterSerializerAttribute` with 3 serialization method in it. This method is usefull for types that application does not own, for example, types defined in other libraries your application has not control for.

In both way the custom serialization code has to include three routines: one to make a deep copy of an object of the type; one to write a tokenized byte representation of an object of the type to a byte stream; and one to recreate a new object of the type from a tokenized byte stream.


## Introduction
As described in [Using Immutable<T> to Optimize Copying](http://dotnet.github.io/orleans/Advanced-Concepts/Using-Immutable-to-Optimize-Copying), Orleans serialization happens in three stages: objects are immediately deep copied to ensure isolation; before being put on the wire, objects are serialized to a message byte stream; and when delivered to the target activation, objects are recreated (deserialized) from the received byte stream. Data types that may be sent in messages -- that is, types that may be passed as method arguments or return values -- must have associated routines that perform these three steps. We refer to these routines collectively as the serializers for a data type.

 The copier for a type stands alone, while the serializer and deserializer are a pair that work together. You can provide just a custom copier, or just a custom serializer and a custom deserializer, or you can provide custom implementations of all three.

 Serializers are registered for each supported data type at silo start-up and whenever an assembly is loaded. Registration is necessary to getting custom serializer routines for a type to be used. Serializer selection is based on the dynamic type of the object to be copied or serialized. For this reason, there is no need to create serializers for abstract classes or interfaces, because they will never be used. 

## When to Consider Writing a Custom Serializer
It is rare that a hand-crafted serializer routine will perform meaningfully better than the generated versions. If you are tempted to do so, you should first consider the following options:

 If there are fields or properties within your data types that don't have to be serialized or copied, you can mark them with the  NonSerialized attribute. This will cause the generated code to skip these fields when copying and serializing. 
 Use Immutable<T> where possible to avoid copying immutable data. See [Using Immutable<T> to Optimize Copying](http://dotnet.github.io/orleans/Advanced-Concepts/Using-Immutable-to-Optimize-Copying) for details. 
 If you're avoiding using the standard generic collection types, don't. The Orleans runtime contains custom serializers for the generic collections that use the semantics of the collections to optimize copying, serializing, and deserializing. These collections also have special "abbreviated" representations in the serialized byte stream, resulting in even more performance advantages. For instance, a `Dictionary<string, string>` will be faster than a `List<Tuple<string, string>>`. 

 The most common case where a custom serializer can provide a noticeable performance gain is when there is significant semantic information encoded in the data type that is not available by simply copying field values. For instance, arrays that are sparsely populated may often be more efficiently serialized by treating the array as a collection of index/value pairs, even if the application keeps the data as a fully realized array for speed of operation.

 A key thing to do before writing a custom serializer is to make sure that the generated serializer is really hurting your performance. Profiling will help a bit here, but even more valuable is running end-to-end stress tests of your application with varying serialization loads to gauge the system-level impact, rather than the micro-impact, of serialization. For instance, building a test version that passes no parameters to or results from grain methods, simply using canned values at either end, will zoom in on the impact of serialization and copying on system performance.

## Method 1: Adding 3 Serialization Routines to your Type

All serializer routines should be implemented as static members of the class or struct they operate on. The names shown here are not required; registration is based on the presence of the respective attributes, not on method names. Note that serializer methods need not be public.

 Unless you implement all three serialization routines, you should mark your type with the  Serializable attribute so that the missing methods will be generated for you.

### Copier
Copier methods are flagged with the Orleans.CopierMethod attribute:

``` csharp
[CopierMethod]}
static private object Copy(object input)
{
    ...
}
```


Copiers are usually the simplest serializer routines to write. They take an object, guaranteed to be of the same type as the type the copier is defined in, and must return a semantically-equivalent copy of the object.

If, as part of copying the object, a sub-object needs to be copied, the best way to do so is to use the SerializationManager's DeepCopyInner routine:


``` csharp
var fooCopy = SerializationManager.DeepCopyInner(foo);
```

It is important to use DeepCopyInner, instead of DeepCopy, in order to maintain the object identity context for the full copy operation.

**Maintaining Object Identity**

An important responsibility of a copy routine is to maintain object identity. The Orleans runtime provides a helper class for this: before copying a sub-object "by hand" (i.e., not by calling DeepCopyInner), check to see if it has already been referenced as follows:


``` csharp
var fooCopy = SerializationContext.Current.CheckObjectWhileCopying(foo);

if (fooCopy == null)
{
    // Actually make a copy of foo
    SerializationContext.Current.RecordObject(foo, fooCopy);
}
```


The last line, the call to RecordObject, is required so that possible future references to the same object as foo references will get found properly by CheckObjectWhileCopying.

Note that this should only be done for class instances, not struct instances or .NET primitives (strings, Uris, enums).

If you use DeepCopyInner to copy sub-objects, then object identity is handled for you.

### Serializer
Serialization methods are flagged with the  SerializerMethod attribute:


``` csharp
[SerializerMethod]
static private void Serialize(object input, BinaryTokenStreamWriter stream, Type expected)
{
    ...
}
```


As with copiers, the "input" object passed to a serializer is guaranteed to be an instance of the defining type. The "expected" type may be ignored; it is based on compile-time type information about the data item, and is used at a higher level to form the type prefix in the byte stream.

To serialize sub-objects, use the SerializationManager's SerializeInner routine:


``` csharp
SerializationManager.SerializeInner(foo, stream, typeof(FooType));
```

If there is no particular expected type for foo, then you can pass null for the expected type.

The BinaryTokenStreamWriter class provides a wide variety of methods for writing data to the byte stream. See the class for documentation. 

**Maintaining Object Identity**

As with copy routines, serialization routines must maintain object identity. If you use SerializerInner, then this is taken care of for you. If not, then before serializing a sub-object, you should do the following:

``` csharp
int reference = SerializationContext.Current.CheckObjectWhileSerializing(foo);

if (reference >= 0)
{
    stream.WriteReference(reference);
    return;
}

SerializationContext.Current.RecordObject(foo, stream.CurrentOffset);
```

 As for copiers, this should only be done for class instances, not struct instances or .NET primitives (strings, Uris, enums), and if you use SerializeInner for sub-objects, then object identity is handled for you.

### Deserializer
Deserialization methods are flagged with the DeserializerMethod attribute:

``` csharp
[DeserializerMethod]
static private object Deserialize(Type expected, BinaryTokenStreamReader stream)
{
    ...
}
```


The "expected" type may be ignored; it is based on compile-time type information about the data item, and is used at a higher level to form the type prefix in the byte stream. The actual type of the object to be created will always be the type of the class in which the deserializer is defined.

 To deserialize sub-objects, use the SerializationManager's DeserializeInner routine:


``` csharp
var foo = SerializationManager.DeserializeInner(typeof(FooType), stream);
```

Or, alternatively,

``` csharp
var foo = SerializationManager.DeserializeInner<FooType>(stream);
```

If there is no particular expected type for foo, use the first call pattern and pass null for the expected type.

The BinaryTokenStreamReader class provides a wide variety of methods for reading data from the byte stream. See the class for documentation. 

**Maintaining Object Identity**

As with serializer routines, deserialization routines must maintain object identity. If you use DeserializerInner, then this is taken care of for you. If not, then before deserializing a sub-object, you should do the following:

``` csharp
// Token is the object introductory token read from the stream
// Typically this is done using stream.TryReadSimpleType
if (token == SerializationTokenType.Reference)
{
    var offset = stream.ReadInt();
    var foo = DeserializationContext.Current.FetchReferencedObject(offset);
}
else
{
    // Deserialize foo
}
```

 If you use DeserializeInner for sub-objects, then object identity is handled for you.

## Method 2: Writing a Special Serialization Class 

In this method you write a new class annotated with an attribute `RegisterSerializerAttribute` and put the 3 serialization routines in it. The rules for how to write those routines are identical to method 1. The only difference is how they are hooked up with the rest of Orleans serialization framework. Below is an example for such a class:


``` csharp
[Orleans.CodeGeneration.RegisterSerializerAttribute()]
internal class MyObjectSerializationExample
{
    static MyObjectSerializationExample()
    {
       Register();
    }

    public static object DeepCopier(object original)
    {
        ...
    }

    public static void Serializer(object untypedInput, BinaryTokenStreamWriter stream, Type expected)
    {
        ...
   }

    public static object Deserializer(Type expected, BinaryTokenStreamReader stream)
    {
        ...
    }

    public static void Register()
    {
        SerializationManager.Register(typeof(MyObject), DeepCopier, Serializer, Deserializer);
    }
}
```

## Hints for Writing Serializers and Deserializers
Often the simplest way to write a serializer/deserializer pair is to serialize by constructing a byte array and writing the array length to the stream, followed by the array itself, and then deserialize by reversing the process. If the array is fixed-length, you can omit it from the stream. This works well when you have a data type that you can represent compactly and that doesn't have sub-objects that might be duplicated (so you don't have to worry about object identity).

Another approach, which is the approach the Orleans runtime takes for collections such as dictionaries, works well for classes with significant and complex internal structure: use instance methods to access the semantic content of the object, serialize that content, and deserialize by setting the semantic contents rather than the complex internal state. In this approach, inner objects are written using SerializeInner and read using DeserializeInner. In this case, it is common to write a custom copier, as well.

If you write a custom serializer, and it winds up looking like a sequence of calls to SerializeInner for erach field in the class, you don't need a custom serializer for that class.

