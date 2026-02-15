# Microsoft Orleans Serialization for MemoryPack

## Introduction
Microsoft Orleans Serialization for MemoryPack provides MemoryPack serialization support for Microsoft Orleans using the MemoryPack format. This high-performance binary serialization format is ideal for scenarios requiring efficient serialization and deserialization.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Serialization.MemoryPack
```

## Example - Configuring MemoryPack Serialization
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
            // Configure MemoryPack as a serializer
            .AddSerializer(serializerBuilder => serializerBuilder.AddMemoryPackSerializer());
    });

// Run the host
await builder.RunAsync();
```

## Example - Using MemoryPack with a Custom Type
```csharp
using Orleans;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using MemoryPack;
namespace ExampleGrains;

// Define a class with MemoryPack attributes
[MemoryPackable]
public partial class MyMemoryPackClass
{
    public string Name { get; set; }
    public int Age { get; set; }
    public List<string> Tags { get; set; }
}

// You can use it directly in your grain interfaces and implementation
public interface IMyGrain : IGrainWithStringKey
{
    Task<MyMemoryPackClass> GetData();
    Task SetData(MyMemoryPackClass data);
}

public class MyGrain : Grain, IMyGrain
{
    private MyMemoryPackClass _data;

    public Task<MyMemoryPackClass> GetData()
    {
        return Task.FromResult(_data);
    }

    public Task SetData(MyMemoryPackClass data)
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
- [MemoryPack for C#](https://github.com/Cysharp/MemoryPack)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
