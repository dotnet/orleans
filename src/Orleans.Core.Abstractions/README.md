# Microsoft Orleans Core Abstractions

## Introduction
Microsoft Orleans Core Abstractions is the foundational library for Orleans containing the public programming APIs for implementing grains and client code. This package defines the core abstractions that form the Orleans programming model, including grain interfaces, grain reference interfaces, and attributes.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Core.Abstractions
```

This package is a dependency of both client and silo (server) applications and is automatically included when you reference the Orleans SDK or the Orleans client/server metapackages.

## Example - Defining a Grain Interface
```csharp
using Orleans;

namespace MyGrainInterfaces;

public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string greeting);
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Grain interfaces](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-interfaces)
- [Grain references](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-references)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)