# Microsoft Orleans Clustering for DynamoDB

## Introduction
Microsoft Orleans Clustering for DynamoDB provides cluster membership functionality for Microsoft Orleans using Amazon's DynamoDB. This allows Orleans silos to coordinate and form a cluster using DynamoDB as the backing store.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Clustering.DynamoDB
```

## Example - Configuring DynamoDB Membership
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace ExampleGrains;

// Define a grain interface
public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string greeting);
}

// Implement the grain interface
public class HelloGrain : Grain, IHelloGrain
{
    public Task<string> SayHello(string greeting)
    {
        return Task.FromResult($"Hello, {greeting}!");
    }
}

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            // Configure DynamoDB clustering
            .UseDynamoDBClustering(options =>
            {
                options.AccessKey = "YOUR_AWS_ACCESS_KEY";
                options.SecretKey = "YOUR_AWS_SECRET_KEY";
                options.Region = "us-east-1";
                options.TableName = "OrleansClusteringTable";
                options.CreateIfNotExists = true;
            });
    });

var host = builder.Build();
await host.StartAsync();

// Get a reference to a grain and call it
var client = host.Services.GetRequiredService<IClusterClient>();
var grain = client.GetGrain<IHelloGrain>("user123");
var response = await grain.SayHello("DynamoDB");

// Print the result
Console.WriteLine($"Grain response: {response}");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Configuration Guide](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/)
- [Orleans Clustering](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management)
- [AWS SDK for .NET Documentation](https://docs.aws.amazon.com/sdk-for-net/index.html)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)