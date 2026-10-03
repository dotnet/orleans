# Microsoft Orleans Serialization

## Introduction
Microsoft Orleans Serialization is a fast, flexible, and version-tolerant serializer for .NET. It provides the core serialization capabilities for Orleans, enabling efficient serialization and deserialization of data across the network and for storage.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Serialization
```

This package is automatically included when you reference the Orleans SDK or the Orleans client/server metapackages.

## Example

```csharp
// Creating a serializer
var services = new ServiceCollection();
services.AddSerializer();
var serviceProvider = services.BuildServiceProvider();
var serializer = serviceProvider.GetRequiredService<Serializer>();

// Serializing an object
var bytes = serializer.SerializeToArray(myObject);

// Deserializing an object
var deserializedObject = serializer.Deserialize<MyType>(bytes);
```

## Supporting your own Types

To make your types serializable in Orleans, mark them with the `[GenerateSerializer]` attribute and mark each field/property which should be serialized with the `[Id(int)]` attribute:

```csharp
[GenerateSerializer]
public class MyClass
{
    [Id(0)]
    public string Name { get; set; }
    
    [Id(1)]
    public int Value { get; set; }
}
```

When fail-closed type validation is enabled, additional types can be allowed by configuring `TypeManifestOptions.AddAllowedType` or `TypeManifestOptions.AddAllowedAssembly`.

Generated serializers and copiers for C# payloads use ref-returning
`UnsafeAccessor` methods to read and restore private fields, readonly fields,
and auto-property backing fields on supported targets. Non-generic C# payloads
use this access on .NET 8 and later; generic C# payloads use it on .NET 9 and
later, with the payload's generic constraints preserved. Struct receivers are
passed by reference. This allows NativeAOT-compiled generated code for C#
payloads to restore get-only and init-only properties and to deep-copy values
stored in readonly fields.

Generated C# codecs and copiers emit one ref-returning accessor per field for
both reading and writing on supported targets.

The generator selects these capabilities from the SDK's target-framework
identifier and version and verifies that `UnsafeAccessorAttribute` is available
in the compilation references.

For C# payloads, legacy targets and .NET 8 generic payloads use generated
field-access delegates. Setting `OrleansHotReload=true` retains lazily
initialized delegates for fields, so existing serializer and copier instances
can access members added by hot reload.

Volatile fields use ref-returning accessors on .NET 10 and later. Earlier
targets restore private volatile fields through generated delegates, preserving
compatibility with runtimes affected by volatile-field signature matching.

F# record and union field restoration uses the existing generated-delegate
strategy on JIT-enabled runtimes.

NativeAOT applications also need statically available codecs, copiers, and
activators for their closed payload types.

## Generated manifest metadata

The source generator registers each serialization contract with its implementation
and target type. Field codecs, base codecs, value serializers, deep and base copiers,
activators, and converters have explicit entries, including both the value and surrogate
types for converters. The registration APIs preserve implementation constructors and
target interface metadata. `TypeConverter` and `CodecProvider` consume these entries
directly during `AddSerializer` service initialization.

Types marked with `[GenerateSerializer]` and implementations marked with
`[RegisterSerializer]`, `[RegisterCopier]`, `[RegisterActivator]`, or
`[RegisterConverter]` receive these registrations automatically. Manual registrations
can use the target-taking `TypeManifestOptions.Add*` overloads. Existing single-type
registrations retain their interface-discovery behavior.

Parameterized array contracts and generic converter surrogates use
`SerializationType` descriptions. These record concrete types, generic parameter
indices, and array shapes so the runtime can bind the selected implementation's
generic arguments directly. Generated registrations preserve nested argument shapes
and parameter ordering. For a described generic target, the runtime matches the
requested closed type against that shape and binds implementation parameters,
preserving fixed arguments and reordered parameters.

Contract lookup selects exact closed targets first, then matching named generic
targets, then array and bare-parameter patterns. Matching patterns use reverse
registration order and bind the requested type's element shape and implementation
parameters before activating the selected codec, copier, or converter.
The selected registration supplies the arguments for implementation closure:
plain open-target entries use positional arguments, and described entries use
their matched parameter bindings, including when both belong to one implementation.

`SerializationType.Array` describes a structural target-matching pattern, including
generic element parameters. Executable array types use source-known closed
descriptors such as `SerializationType.Create(typeof(MyValue[]))`. The generator
emits these concrete descriptors for fully known array shapes, including arrays
nested in partly generic contracts. Target-taking registrations can likewise supply
closed codec, copier, and converter types and concrete surrogate types.

Resolving an array matching pattern as an executable type reports
`NotSupportedException` with closed-registration guidance on both JIT and NativeAOT
runtimes. Register each closed converter/surrogate combination used by an executable
surrogate description containing parameterized arrays. This gives both runtimes the
same registration contract and supplies the native array representations through
typed code and source-known type references.

NativeAOT applications also provide statically compiled closed codec and serializer
instances for the generic combinations they use. The `Metadata` scenario in
`test/Orleans.NativeAotSmoke` exercises the default manifest and primitive, reference
tuple, and value tuple serialization with closed built-in codec instances.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Serialization in Orleans](https://dotnet.github.io/orleans/docs/host/configuration-guide/serialization/)
- [Configure serialization](https://dotnet.github.io/orleans/docs/host/configuration-guide/serialization-configuration/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)