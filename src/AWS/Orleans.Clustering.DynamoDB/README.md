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
                options.Service = "us-east-1";
                options.TableName = "OrleansClusteringTable";
                options.UseProvisionedThroughput = false;
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

The AWS SDK credential chain supplies credentials when explicit keys and a profile are omitted. `ProfileName` selects a named profile. `AccessKey`, `SecretKey`, and `Token` configure explicit session credentials.

## Aspire

AWS Aspire can run DynamoDB Local and provide CDK or CloudFormation table outputs to Orleans. See [Use Amazon DynamoDB with Aspire](https://dotnet.github.io/orleans/docs/host/dynamodb-aspire/) for clustering, grain storage, reminders, identity, and table lifecycle guidance.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Configuration Guide](https://dotnet.github.io/orleans/docs/host/configuration-guide/)
- [Orleans Clustering](https://dotnet.github.io/orleans/docs/implementation/cluster-management/)
- [AWS SDK for .NET Documentation](https://docs.aws.amazon.com/sdk-for-net/index.html)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)