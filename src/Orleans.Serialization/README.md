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