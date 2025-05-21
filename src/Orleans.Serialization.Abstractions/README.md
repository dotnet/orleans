# Microsoft Orleans Serialization Abstractions

## Introduction
Orleans Serialization Abstractions package provides the core interfaces and attributes needed for Orleans serialization. This package contains the definitions used for serialization but not the serialization implementation itself.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Serialization.Abstractions
```

This package is automatically included when you reference the Orleans Serialization package or Orleans SDK.

## Example

```csharp
using Orleans.Serialization;

// Define a serializable class
[GenerateSerializer]
public class MyData
{
    [Id(0)]
    public string Name { get; set; }
    
    [Id(1)]
    public int Age { get; set; }
    
    [Id(2)]
    public List<string> Tags { get; set; }
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