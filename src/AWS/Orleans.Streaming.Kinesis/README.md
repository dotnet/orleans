# Microsoft Orleans Streaming for Amazon Kinesis

Microsoft Orleans Streaming for Amazon Kinesis provides a persistent stream provider backed by Amazon Kinesis Data Streams.

## Getting started

Install the package from NuGet:

```shell
dotnet add package Microsoft.Orleans.Streaming.Kinesis
```

Configure the provider on the silo:

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddMemoryGrainStorage("PubSubStore")
        .AddKinesisStreams(
            name: "Kinesis",
            configureOptions: options =>
            {
                options.StreamName = "orders";
                options.Region = "us-east-1";
            });
});

await builder.RunAsync();
```

Configure clients which publish to the stream using the same provider name and Kinesis options:

```csharp
clientBuilder.AddKinesisStreams(
    name: "Kinesis",
    configureOptions: options =>
    {
        options.StreamName = "orders";
        options.Region = "us-east-1";
    });
```

When access and secret keys are not configured, the provider uses the standard AWS SDK credential chain.

## Checkpoint persistence

Kinesis shard iterators identify a position temporarily, but Kinesis does not store a committed consumer offset. A consumer must persist the last processed sequence number separately to resume after shutdown or reassignment.

The default silo configuration uses Orleans grains for this purpose. Each shard's sequence number is stored through the configured `PubSubStore` grain storage provider. This design does not require DynamoDB or the Kinesis Client Library and allows any Orleans grain storage provider to persist checkpoints.

Configure a durable `PubSubStore` provider in production. The in-memory provider shown above is suitable only for development because its checkpoints do not survive a cluster restart.
Set `GrainStreamQueueCheckpointerOptions.StorageProviderName` to use another registered grain storage provider.

The grain checkpointer prevents sequence numbers from moving backwards and writes at most once per minute by default. Use the configurator overload to change the persistence interval:

```csharp
using Orleans.Streams;

siloBuilder.AddKinesisStreams("Kinesis", configurator =>
{
    configurator.ConfigureKinesis(options => options.Configure(kinesis =>
    {
        kinesis.StreamName = "orders";
        kinesis.Region = "us-east-1";
    }));
    configurator.UseGrainCheckpointer(options => options.Configure(checkpointer =>
    {
        checkpointer.CheckpointComparer = StreamCheckpointComparers.Numeric;
        checkpointer.PersistInterval = TimeSpan.FromSeconds(30);
    }));
});
```

To persist checkpoints directly in DynamoDB without configuring grain storage, select the DynamoDB table checkpointer:

```csharp
siloBuilder.AddKinesisStreams("Kinesis", configurator =>
{
    configurator.ConfigureKinesis(options => options.Configure(kinesis =>
    {
        kinesis.StreamName = "orders";
        kinesis.Region = "us-east-1";
    }));
    configurator.UseDynamoDBCheckpointer(options =>
    {
        options.Service = "us-east-1";
        options.TableName = "OrleansStreamCheckpoints";
    });
});
```

The DynamoDB checkpointer uses on-demand billing and creates its table by default. It stores one versioned row per service, provider, and shard. Conditional writes prevent a stale silo owner from moving a checkpoint backward. Set `CreateIfNotExists` to `false` when tables are provisioned separately.

To provide a different checkpoint implementation, use the configurator overload and call
`ConfigureCheckpointer<TOptions>` with an `IStreamQueueCheckpointerFactory`.

## Documentation

- [Microsoft Orleans documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans streaming](https://learn.microsoft.com/dotnet/orleans/streaming/)
- [Amazon Kinesis Data Streams documentation](https://docs.aws.amazon.com/streams/latest/dev/introduction.html)
