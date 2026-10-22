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
                    options.Service = "us-east-1";
                    options.ServiceId = "my-application";
                    options.TableName = "OrleansGrainState";
                    options.UseProvisionedThroughput = false;
                });
    });

// Run the host
await builder.RunAsync();
```

## Service identity and upgrades

Set `DynamoDBStorageOptions.ServiceId` explicitly for every provider. It prefixes each grain state key as `{ServiceId}_{grainId}` and must remain stable across deployments that share state. Startup validation reports an `OrleansConfigurationException` when it is unset.

For new deployments, choose a stable application identifier, such as the cluster's `ServiceId`. When upgrading, retain the identifier used to write the existing state. If the deployment used the previous empty-string default, set `ServiceId = string.Empty` in code or `"ServiceId": ""` in configuration to preserve access to those keys. Choosing a different identifier selects a different state namespace; accessing existing state under that prefix requires a separate data migration.

## Endpoints and credentials

`Service` selects an AWS region or an HTTP/HTTPS endpoint URL. `AccessKey` and `SecretKey` supply explicit credentials; adding `Token` supplies session credentials. `ProfileName` selects a named AWS profile when explicit keys are omitted. These credentials apply to both regions and endpoint URLs.

The AWS SDK credential chain supplies credentials for regions and HTTPS endpoints when explicit keys and a profile are omitted. HTTP endpoints use the local-development credentials `dummy` and `dummyKey` in that case. Configure explicit credentials when a DynamoDB Local instance uses an access key to select its database.

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
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Grain Persistence](https://dotnet.github.io/orleans/docs/grains/grain-persistence/)
- [AWS SDK for .NET Documentation](https://docs.aws.amazon.com/sdk-for-net/index.html)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)