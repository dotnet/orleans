# Microsoft Orleans Serialization for Protobuf

## Introduction
Microsoft Orleans Serialization for Protobuf provides Protocol Buffers (Protobuf) serialization support for Microsoft Orleans. Protobuf is a compact, efficient binary serialization format developed by Google, which is ideal for high-performance scenarios requiring efficient serialization and deserialization.

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
```csharp
using Orleans;
using ProtoBuf;

// Define a class with Protobuf attributes
[ProtoContract]
public class MyProtobufClass
{
    [ProtoMember(1)]
    public string Name { get; set; }
    
    [ProtoMember(2)]
    public int Age { get; set; }
    
    [ProtoMember(3)]
    public List<string> Tags { get; set; }
}

// You can use it directly in your grain interfaces and implementation
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

## Example - Using .proto Files
You can also use standard .proto files and generate C# classes:

```proto
// MyData.proto
syntax = "proto3";

option csharp_namespace = "MyApp.Protos";

message Person {
  string name = 1;
  int32 age = 2;
  repeated string tags = 3;
}
```

Then reference the generated classes in your Orleans code:

```csharp
using MyApp.Protos;
using Orleans;

public interface IPersonGrain : IGrainWithStringKey
{
    Task<Person> GetPerson();
    Task SetPerson(Person person);
}

public class PersonGrain : Grain, IPersonGrain
{
    private Person _person;

    public Task<Person> GetPerson()
    {
        return Task.FromResult(_person);
    }

    public Task SetPerson(Person person)
    {
        _person = person;
        return Task.CompletedTask;
    }
}
```

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