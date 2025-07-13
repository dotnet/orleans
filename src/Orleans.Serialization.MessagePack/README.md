# Microsoft Orleans Serialization for MessagePack

## Introduction
Microsoft Orleans Serialization for MessagePack provides MessagePack serialization support for Microsoft Orleans using the MessagePack format. This high-performance binary serialization format is ideal for scenarios requiring efficient serialization and deserialization.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Serialization.MessagePack
```

## Example - Configuring MessagePack Serialization
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
            // Configure MessagePack as a serializer
            .AddSerializer(serializerBuilder => serializerBuilder.AddMessagePackSerializer());
    });

// Run the host
await builder.RunAsync();
```

## Example - Using MessagePack with a Custom Type
```csharp
using Orleans;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using MessagePack;
namespace ExampleGrains;

// Define a class with MessagePack attributes
[MessagePackObject]
public class MyMessagePackClass
{
    [Key(0)]
    public string Name { get; set; }
    
    [Key(1)]
    public int Age { get; set; }
    
    [Key(2)]
    public List<string> Tags { get; set; }
}

// You can use it directly in your grain interfaces and implementation
public interface IMyGrain : IGrainWithStringKey
{
    Task<MyMessagePackClass> GetData();
    Task SetData(MyMessagePackClass data);
}

public class MyGrain : Grain, IMyGrain
{
    private MyMessagePackClass _data;

    public Task<MyMessagePackClass> GetData()
    {
        return Task.FromResult(_data);
    }

    public Task SetData(MyMessagePackClass data)
    {
        _data = data;
        return Task.CompletedTask;
    }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Serialization](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/serialization)
- [MessagePack for C#](https://github.com/neuecc/MessagePack-CSharp)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)