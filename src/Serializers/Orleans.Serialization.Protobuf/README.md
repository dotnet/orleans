# Microsoft Orleans Serialization for Protobuf

## Introduction
Microsoft Orleans Serialization for Protobuf provides Protocol Buffers (Protobuf) serialization support for Microsoft Orleans using **Google.Protobuf**. This package integrates Google's official `Google.Protobuf` library with Orleans, allowing you to use Protocol Buffers messages in your grain interfaces and implementations.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Serialization.Protobuf
```

## Example - Configuring Protobuf Serialization
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Serialization;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Protobuf as a serializer
            .AddSerializer(serializerBuilder => serializerBuilder.AddProtobufSerializer());
    });

// Run the host
await builder.RunAsync();
```

## Using Protobuf Types with Orleans
This package supports types generated from `.proto` files using Google.Protobuf. For detailed information on creating Protobuf messages and configuring your project, see [Create Protobuf messages for .NET apps](https://learn.microsoft.com/en-us/aspnet/core/grpc/protobuf).

Once you have defined your Protobuf messages and configured code generation, you can use them directly in your grain interfaces:

```csharp
using Orleans;
using MyApp.Models;

public interface IMyGrain : IGrainWithStringKey
{
    Task<MyProtobufClass> GetData();
    Task SetData(MyProtobufClass data);
}

public class MyGrain : Grain, IMyGrain
{
    private MyProtobufClass _data;

    public Task<MyProtobufClass> GetData() => Task.FromResult(_data);
    public Task SetData(MyProtobufClass data)
    {
        _data = data;
        return Task.CompletedTask;
    }
}
```

**Note:** Google.Protobuf collection types (`RepeatedField<T>`, `MapField<TKey, TValue>`, and `ByteString`) are automatically supported.

## Documentation
For more comprehensive documentation, please refer to:
- [Create Protobuf messages for .NET apps](https://learn.microsoft.com/en-us/aspnet/core/grpc/protobuf) - Official guide for working with Protobuf in .NET
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Serialization](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/serialization)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)