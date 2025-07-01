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

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Serialization in Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/serialization)
- [Orleans type serialization](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/serialization-attributes)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)