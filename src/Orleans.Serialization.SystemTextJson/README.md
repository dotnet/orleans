# Microsoft Orleans Serialization for System.Text.Json

## Introduction
Microsoft Orleans Serialization for System.Text.Json provides JSON serialization support for Microsoft Orleans using the modern System.Text.Json library. This allows you to use the high-performance JSON serialization capabilities from the .NET runtime within your Orleans application.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Serialization.SystemTextJson
```

## Example - Configuring System.Text.Json Serialization
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
            // Configure System.Text.Json as a serializer
            .AddSerializer(serializerBuilder => serializerBuilder.AddSystemTextJsonSerializer(type => type.Namespace.StartsWith("ExampleGrains")));
    });

// Run the host
await builder.RunAsync();
```

## Example - Using System.Text.Json with a Custom Type
```csharp
using Orleans;
using System.Text.Json.Serialization;
namespace ExampleGrains;

// Define a class with System.Text.Json attributes
public class MyJsonClass
{
    [JsonPropertyName("full_name")]
    public string Name { get; set; }
    
    [JsonPropertyName("age")]
    public int Age { get; set; }
    
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; }
    
    [JsonIgnore]
    public string SecretData { get; set; }
}

// You can use it directly in your grain interfaces and implementation
public interface IMyGrain : IGrainWithStringKey
{
    Task<MyJsonClass> GetData();
    Task SetData(MyJsonClass data);
}

public class MyGrain : Grain, IMyGrain
{
    private MyJsonClass _data;

    public Task<MyJsonClass> GetData()
    {
        return Task.FromResult(_data);
    }

    public Task SetData(MyJsonClass data)
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
- [System.Text.Json Documentation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/overview)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)