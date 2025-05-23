# Microsoft Orleans Persistence for DynamoDB

## Introduction
Microsoft Orleans Persistence for DynamoDB provides grain persistence for Microsoft Orleans using Amazon's DynamoDB. This allows your grains to persist their state in DynamoDB and reload it when they are reactivated.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Persistence.DynamoDB
```

## Example - Configuring DynamoDB Persistence
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure DynamoDB as grain storage
            .AddDynamoDBGrainStorage(
                name: "dynamoStore",
                configureOptions: options =>
                {
                    options.AccessKey = "YOUR_AWS_ACCESS_KEY";
                    options.SecretKey = "YOUR_AWS_SECRET_KEY";
                    options.Region = "us-east-1";
                    options.TableName = "OrleansGrainState";
                    options.CreateIfNotExists = true;
                });
    });

// Run the host
await builder.RunAsync();
```

## Example - Using Grain Storage in a Grain
```csharp
// Define grain state class

public class MyGrainState
{
    public string Data { get; set; }
    public int Version { get; set; }
}

// Grain implementation that uses the DynamoDB storage
public class MyGrain : Grain, IMyGrain, IGrainWithStringKey
{
    private readonly IPersistentState<MyGrainState> _state;

    public MyGrain([PersistentState("state", "dynamoStore")] IPersistentState<MyGrainState> state)
    {
        _state = state;
    }

    public async Task SetData(string data)
    {
        _state.State.Data = data;
        _state.State.Version++;
        await _state.WriteStateAsync();
    }

    public Task<string> GetData()
    {
        return Task.FromResult(_state.State.Data);
    }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Grain Persistence](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence)
- [AWS SDK for .NET Documentation](https://docs.aws.amazon.com/sdk-for-net/index.html)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)