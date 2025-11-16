# Microsoft Orleans Serialization for Protobuf

## Introduction
Microsoft Orleans Serialization for Protobuf provides Protocol Buffers (Protobuf) serialization support for Microsoft Orleans using **Google.Protobuf**. Protobuf is a compact, efficient binary serialization format developed by Google, which is ideal for high-performance scenarios requiring efficient serialization and deserialization.

This package integrates Google's official `Google.Protobuf` library with Orleans, allowing you to use Protocol Buffers messages (generated from `.proto` files) in your grain interfaces and implementations.

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

## Example - Using Protobuf with a Custom Type
This package supports Google.Protobuf types, which are defined using `.proto` files and generated using the Protocol Buffers compiler.

First, define your message in a `.proto` file:

```proto
// MyData.proto
syntax = "proto3";

option csharp_namespace = "MyApp.Models";

message MyProtobufClass {
  string name = 1;
  int32 age = 2;
  repeated string tags = 3;
}
```

Configure your project to generate C# classes from the `.proto` file by adding the following to your `.csproj`:

```xml
<ItemGroup>
  <Protobuf Include="MyData.proto" GrpcServices="None" />
  <PackageReference Include="Google.Protobuf" Version="3.25.0" />
  <PackageReference Include="Grpc.Tools" Version="2.60.0" PrivateAssets="All" />
</ItemGroup>
```

Then use the generated classes in your grain interfaces and implementation:

```csharp
using Orleans;
using MyApp.Models;

// The generated class implements IMessage from Google.Protobuf
// and can be used directly in your grain interfaces
public interface IMyGrain : IGrainWithStringKey
{
    Task<MyProtobufClass> GetData();
    Task SetData(MyProtobufClass data);
}

public class MyGrain : Grain, IMyGrain
{
    private MyProtobufClass _data;

    public Task<MyProtobufClass> GetData()
    {
        return Task.FromResult(_data);
    }

    public Task SetData(MyProtobufClass data)
    {
        _data = data;
        return Task.CompletedTask;
    }
}
```

## Using Google.Protobuf Collections
Google.Protobuf provides specialized collection types that are automatically supported:

```proto
syntax = "proto3";
option csharp_namespace = "MyApp.Models";

message ComplexData {
  // RepeatedField<T> for repeated fields
  repeated string items = 1;
  
  // MapField<K, V> for map fields
  map<string, int32> counts = 2;
  
  // ByteString for binary data
  bytes data = 3;
}
```

These collections (`RepeatedField<T>`, `MapField<TKey, TValue>`, and `ByteString`) are automatically serialized and copied by the Orleans.Serialization.Protobuf package.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Serialization](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/serialization)
- [Protocol Buffers Documentation](https://developers.google.com/protocol-buffers/docs/overview)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)